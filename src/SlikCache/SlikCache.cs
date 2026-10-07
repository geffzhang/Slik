using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using DotNext.Threading;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("SlikCache.Tests")]
[assembly: InternalsVisibleTo("SlikCache.IntegrationTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]

namespace Slik.Cache
{
    /// <summary>
    /// Distributed Cache Implementation 
    /// </summary>
    internal partial class SlikCache : SimpleStateMachine, IDistributedCache
    {
        private readonly MemoryDistributedCache _internalCache;
        private readonly ConcurrentDictionary<string, byte> _slidingExpirations = new();
        private readonly ConcurrentDictionary<string, byte> _keys = new();
        private readonly ILogger<SlikCache> _logger;
        private readonly NamedLockFactory _lockFactory = new();
        private Guid _recordBeingAppendedLocally;
        private readonly int _recordsPerPartition;
        private int _recordsSinceSnapshot;

        public TimeSpan CommitTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public string LogLocation { get; }

        public SlikCache(IOptions<SlikOptions> options, ILoggerFactory loggerFactory)
            : this(options.Value, loggerFactory)
        {
        }

        private SlikCache(SlikOptions options, ILoggerFactory loggerFactory)
            : base(new DirectoryInfo(Path.Combine(options.DataFolder, "Cache-v6", "Snapshots")))
        {
            if (options.RecordsPerPartition <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "RecordsPerPartition must be positive.");

            _recordsPerPartition = options.RecordsPerPartition;
            LogLocation = Path.Combine(options.DataFolder, "Cache-v6");
            _logger = loggerFactory.CreateLogger<SlikCache>();
            _internalCache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()), loggerFactory);
        }

        protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (!entry.TryGetPayload(out var payload) || payload.IsEmpty)
                return false;

            var record = JsonSerializer.Deserialize<CacheLogRecord>(payload.ToArray())
                ?? throw new InvalidDataException($"Unable to deserialize cache log entry at index {entry.Index}.");

            if (record.Id == _recordBeingAppendedLocally)
            {
                await ApplyRecordAsync(record, token).ConfigureAwait(false);
            }
            else
            {
                using (await _lockFactory.AcquireWriteLockAsync(record.Key, token).ConfigureAwait(false))
                    await ApplyRecordAsync(record, token).ConfigureAwait(false);
            }

            _recordsSinceSnapshot++;
            return _recordsSinceSnapshot >= _recordsPerPartition;
        }

        private async Task ApplyRecordAsync(CacheLogRecord record, CancellationToken token)
        {
            switch (record.Operation)
            {
                case CacheOperation.Update:
                    await _internalCache.SetAsync(record.Key, record.Value, record.Options ?? new(), token).ConfigureAwait(false);
                    _keys.TryAdd(record.Key, 0);
                    if (record.Options?.SlidingExpiration is not null)
                        _slidingExpirations.TryAdd(record.Key, 0);
                    else
                        _slidingExpirations.TryRemove(record.Key, out _);
                    break;
                case CacheOperation.Remove:
                    await _internalCache.RemoveAsync(record.Key, token).ConfigureAwait(false);
                    _keys.TryRemove(record.Key, out _);
                    _slidingExpirations.TryRemove(record.Key, out _);
                    break;
                case CacheOperation.Refresh:
                    await _internalCache.RefreshAsync(record.Key, token).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported cache operation '{record.Operation}'.");
            }
        }

        protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        {
            var keys = _keys.Keys.ToArray();
            var records = new List<CacheLogRecord>(keys.Length);
            foreach (string key in keys)
            {
                var value = await _internalCache.GetAsync(key, token).ConfigureAwait(false);
                if (value is not null)
                    records.Add(new CacheLogRecord(CacheOperation.Update, key, value));
            }

            await writer.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(records), token: token).ConfigureAwait(false);
        }

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
        {
            var snapshot = await File.ReadAllBytesAsync(snapshotFile.FullName, token).ConfigureAwait(false);
            var records = JsonSerializer.Deserialize<CacheLogRecord[]>(snapshot)
                ?? throw new InvalidDataException($"Unable to deserialize cache snapshot '{snapshotFile.FullName}'.");

            foreach (var record in records)
                await ApplyRecordAsync(record, token).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            await _lockFactory.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        // delegate to handle leader redirection
        internal event Func<CacheLogRecord, CancellationToken, ValueTask<bool>>? RedirectHandler;
        internal event Func<CacheLogRecord, CancellationToken, ValueTask>? ReplicateHandler;

        public class RemoteUpdateException : Exception
        {
            public RemoteUpdateException(string message) : base(message) { }
        }

        #region IDistributedCache implementation

        public byte[]? Get(string key) => GetAsync(key).Result;

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            _logger.LogDebug($"Reading entry '{key}'");

            using (await _lockFactory.AcquireReadLockAsync(key, token).ConfigureAwait(false))
            {
                var result = await _internalCache.GetAsync(key, token).ConfigureAwait(false);

                // if there is a sliding expiration, refresh it
                if (_slidingExpirations.ContainsKey(key))
                {
                    _ = BroadcastRefreshAsync(key, token);
                }

                return result;
            }
        }

        public void Refresh(string key) => RefreshAsync(key).Wait();

        public async Task RefreshAsync(string key, CancellationToken token = default)
        {
            await _internalCache.RefreshAsync(key, token).ConfigureAwait(false);
            await BroadcastRefreshAsync(key, token).ConfigureAwait(false);
        }

        private async Task BroadcastRefreshAsync(string key, CancellationToken token = default)
        {
            var record = new CacheLogRecord(CacheOperation.Refresh, key, Array.Empty<byte>())
            {
                Id = Guid.NewGuid()
            }; 

            await RedirectApplyReplicateAsync(record, () => Task.FromResult(Array.Empty<byte>()), token).ConfigureAwait(false);
        }

        private async Task RedirectApplyReplicateAsync(CacheLogRecord record, Func<Task<byte[]>> localUpdateAction, CancellationToken token = default)
        {
            bool handled = false;

            do
            {
                handled = RedirectHandler != null && await RedirectHandler(record, token).ConfigureAwait(false);

                if (!handled)
                {
                    _logger.LogDebug("The change is not handled by the router, applying locally");

                    using (await _lockFactory.AcquireWriteLockAsync(record.Key, token).ConfigureAwait(false))
                    {
                        _recordBeingAppendedLocally = record.Id;
                        try
                        {
                            var fallbackValue = await localUpdateAction().ConfigureAwait(false);                            
                            
                            if (ReplicateHandler != null) // not in offline mode
                            {
                                try
                                {
                                    await ReplicateHandler(record, token).ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    _logger.LogWarning(e,
                                        "Error while updating remote storages. Rolling back the uncommitted cache change.");

                                    if (record.Operation != CacheOperation.Refresh)
                                    {
                                        if (fallbackValue != null && fallbackValue.Length > 0)
                                        {
                                            await _internalCache.SetAsync(record.Key, fallbackValue, record.Options ?? new(), token);
                                            _keys.TryAdd(record.Key, 0);
                                        }
                                        else
                                        {
                                            await _internalCache.RemoveAsync(record.Key, token);
                                            _keys.TryRemove(record.Key, out _);
                                        }
                                    }

                                    handled = false;
                                }
                            }

                            handled = true;
                        }
                        finally
                        {
                            _recordBeingAppendedLocally = default;
                        }
                    }
                }

                if (!handled)
                    _logger.LogDebug("Retrying to redirect or apply locally after a failure");

            } while (!handled);
        }        

        public void Remove(string key) => RemoveAsync(key).Wait();

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            _logger.LogDebug($"Removing entry '{key}'");

            var record = new CacheLogRecord(CacheOperation.Remove, key, Array.Empty<byte>())
            {
                Id = Guid.NewGuid()
            };

            await RedirectApplyReplicateAsync(record, async () =>
            {
                var oldValue = await _internalCache.GetAsync(key, token).ConfigureAwait(false);
                if (oldValue != null)
                {
                    await _internalCache.RemoveAsync(key, token).ConfigureAwait(false);
                    _keys.TryRemove(key, out _);
                    _slidingExpirations.TryRemove(key, out _);
                }
                return oldValue ?? Array.Empty<byte>();
            }, token).ConfigureAwait(false);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions? options) => SetAsync(key, value, options).Wait();

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions? options, CancellationToken token = default)
        {
            _logger.LogDebug($"Updating entry '{key}'");

            var record = new CacheLogRecord(CacheOperation.Update, key, value, options)
            {
                Id = Guid.NewGuid()
            };

            await RedirectApplyReplicateAsync(record, async () =>
            {
                var oldValue = await _internalCache.GetAsync(key, token).ConfigureAwait(false);                
                await _internalCache.SetAsync(key, value, options ?? new(), token);
                _keys.TryAdd(key, 0);

                if (options?.SlidingExpiration != null)
                    _slidingExpirations.TryAdd(key, 0);
                else
                    _slidingExpirations.TryRemove(key, out _);

                return oldValue ?? Array.Empty<byte>();
            }, token).ConfigureAwait(false);
        }
        #endregion
    }
}