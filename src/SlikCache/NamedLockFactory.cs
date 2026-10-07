using DotNext.Threading;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Slik.Cache
{
    public class NamedLockFactory : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, AsyncReaderWriterLock> _locks = new();

        public readonly struct LockScope : IDisposable
        {
            private readonly AsyncReaderWriterLock? _lock;

            internal LockScope(AsyncReaderWriterLock @lock) => _lock = @lock;

            public void Dispose() => _lock?.Release();
        }

        public async Task<LockScope> AcquireWriteLockAsync(string name, CancellationToken token = default)
        {
            var namedLock = _locks.GetOrAdd(name, _ => new AsyncReaderWriterLock());
            await namedLock.EnterWriteLockAsync(token).ConfigureAwait(false);
            return new LockScope(namedLock);
        }

        public async Task<LockScope> AcquireReadLockAsync(string name, CancellationToken token = default)
        {
            var namedLock = _locks.GetOrAdd(name, _ => new AsyncReaderWriterLock());
            await namedLock.EnterReadLockAsync(token).ConfigureAwait(false);
            return new LockScope(namedLock);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            GC.SuppressFinalize(this);
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            foreach (var namedLock in _locks.Values)
                await namedLock.DisposeAsync();
        }
    }
}