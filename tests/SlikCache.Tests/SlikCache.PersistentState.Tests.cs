using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Slik.Cache.Tests
{
    [TestClass]
    public class SlikCachePersistentStateTests
    {
        [TestMethod]
        public async Task RestoreAsync_SnapshotRestoresUpdatedAndRemovedEntries()
        {
            const string key1 = "key1";
            const string key2 = "key2";
            byte[] expectedValue1 = new byte[] { 3 };
            string logLocation = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

            try
            {
                var cache = new SlikCache(
                    Options.Create(new SlikOptions { DataFolder = logLocation }),
                    loggerFactory);
                await cache.RestoreAsync(CancellationToken.None);

                await using (var log = new WriteAheadLog(
                    new WriteAheadLog.Options { Location = cache.LogLocation },
                    cache))
                {
                    await log.InitializeAsync(CancellationToken.None);
                    await AppendAndCommitAsync(log, new BinaryLogEntry { Term = 1, Content = Array.Empty<byte>() });
                    await AppendAndCommitAsync(log, new CacheLogRecord(CacheOperation.Update, key1, new byte[] { 1 }));
                    await AppendAndCommitAsync(log, new CacheLogRecord(CacheOperation.Update, key2, new byte[] { 2 }));
                    await AppendAndCommitAsync(log, new CacheLogRecord(CacheOperation.Update, key1, expectedValue1));
                    await AppendAndCommitAsync(log, new CacheLogRecord(CacheOperation.Remove, key2, Array.Empty<byte>()));
                    await log.FlushAsync(CancellationToken.None);
                }

                await cache.DisposeAsync();

                var restoredCache = new SlikCache(
                    Options.Create(new SlikOptions { DataFolder = logLocation }),
                    loggerFactory);
                await restoredCache.RestoreAsync(CancellationToken.None);

                await using (var restoredLog = new WriteAheadLog(
                    new WriteAheadLog.Options { Location = restoredCache.LogLocation },
                    restoredCache))
                {
                    await restoredLog.InitializeAsync(CancellationToken.None);

                    var actualValue1 = await restoredCache.GetAsync(key1) ?? throw new NullReferenceException();
                    Assert.IsTrue(expectedValue1.SequenceEqual(actualValue1));
                    Assert.IsNull(await restoredCache.GetAsync(key2));
                }

                await restoredCache.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(logLocation))
                    Directory.Delete(logLocation, true);
            }
        }

        private static async Task AppendAndCommitAsync(WriteAheadLog log, CacheLogRecord record)
            => await AppendAndCommitAsync(log, new BinaryLogEntry
            {
                Term = 1,
                Content = JsonSerializer.SerializeToUtf8Bytes(record)
            });

        private static async Task AppendAndCommitAsync(WriteAheadLog log, BinaryLogEntry entry)
        {
            var index = await log.AppendAsync(entry, CancellationToken.None);
            await log.CommitAsync(index, CancellationToken.None);
            await log.WaitForApplyAsync(index, CancellationToken.None);
        }
    }
}
