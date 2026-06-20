using System.Collections.Concurrent;

namespace Kaimo_File_Server.Core.Services
{
    /// <summary>
    /// In-memory lock per share name.
    /// Prevents concurrent file operations while renames or deletions are in progress.
    /// Intended for single-instance deployments.
    /// </summary>
    public class ShareLockManager
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public SemaphoreSlim GetLock(string shareName)
            => _locks.GetOrAdd(shareName, _ => new SemaphoreSlim(1, 1));

        public void RemoveLock(string shareName)
        {
            if (_locks.TryRemove(shareName, out var sem))
                sem.Dispose();
        }

        public void RenameLock(string oldName, string newName)
        {
            if (_locks.TryRemove(oldName, out var sem))
                _locks.TryAdd(newName, sem);
        }
    }
}