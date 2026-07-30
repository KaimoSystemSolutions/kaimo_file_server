using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>
/// Coordinates one snapshot token between bridge materialization, native Samba
/// readers, and cache eviction.
///
/// The per-key gate serializes materialization/reconciliation inside the bridge.
/// A Unix <c>flock</c> on the token's lease file extends the boundary across
/// processes: bridge materializers and cleanup hold exclusive locks, while
/// Samba handles hold shared locks.
/// </summary>
public sealed class SnapshotCacheLeaseManager
{
    private readonly ConcurrentDictionary<LeaseKey, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, HandoffLease> _handoffs = new();
    private readonly ConcurrentDictionary<LeaseKey, int> _handoffCounts = new();
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

    internal async ValueTask<MaterializationLease> AcquireMaterializationAsync(
        string cacheRoot,
        Guid shareId,
        string gmtToken,
        CancellationToken cancellationToken = default)
    {
        var key = new LeaseKey(shareId.ToString("N"), gmtToken);
        Entry entry = Rent(key);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Return(key, entry);
            throw;
        }

        try
        {
            while (_handoffCounts.ContainsKey(key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }
            string tokenRoot = SnapshotCache.EnsureTokenRoot(
                cacheRoot, shareId, gmtToken);
            FileLease fileLease = await FileLease.AcquireExclusiveAsync(
                SnapshotCache.EnsureLeaseFile(tokenRoot), cancellationToken);
            return new MaterializationLease(this, key, entry, fileLease);
        }
        catch
        {
            entry.Gate.Release();
            Return(key, entry);
            throw;
        }
    }

    /// <summary>
    /// Reuses an already complete immutable projection without waiting for
    /// existing native readers to release their shared leases. The keyed gate
    /// excludes bridge materializers while <paramref name="isValid"/> runs; the
    /// shared file lease excludes cleanup. A null result means the caller must
    /// take the normal exclusive materialization path.
    /// </summary>
    internal async ValueTask<string?> TryAcquireExistingHandoffAsync(
        string cacheRoot,
        Guid shareId,
        string gmtToken,
        Func<CancellationToken, ValueTask<bool>> isValid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isValid);

        var key = new LeaseKey(shareId.ToString("N"), gmtToken);
        Entry entry = Rent(key);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Return(key, entry);
            throw;
        }

        FileLease? fileLease = null;
        try
        {
            string tokenRoot = SnapshotCache.TokenRootFor(
                cacheRoot, key.ShareScope, gmtToken);
            string leasePath = Path.Combine(
                tokenRoot, SnapshotCache.LeaseFileName);
            if (!Directory.Exists(tokenRoot) || !File.Exists(leasePath))
                return null;

            fileLease = await FileLease.AcquireSharedAsync(
                leasePath, cancellationToken);
            if (!await isValid(cancellationToken))
                return null;

            string leaseId = RegisterHandoff(key, fileLease);
            fileLease = null;
            return leaseId;
        }
        finally
        {
            fileLease?.Dispose();
            entry.Gate.Release();
            Return(key, entry);
        }
    }

    /// <summary>
    /// Attempts to exclude materializers and native shared leases without
    /// waiting. A null result means cleanup must skip this active token.
    /// </summary>
    internal IDisposable? TryAcquireEviction(
        string cacheRoot,
        string shareScope,
        string gmtToken)
    {
        var key = new LeaseKey(shareScope, gmtToken);
        Entry entry = Rent(key);
        if (!entry.Gate.Wait(0))
        {
            Return(key, entry);
            return null;
        }

        try
        {
            if (_handoffCounts.ContainsKey(key))
            {
                entry.Gate.Release();
                Return(key, entry);
                return null;
            }
            string tokenRoot = SnapshotCache.TokenRootFor(
                cacheRoot, shareScope, gmtToken);
            if (!Directory.Exists(tokenRoot))
            {
                entry.Gate.Release();
                Return(key, entry);
                return null;
            }
            string leasePath = SnapshotCache.EnsureLeaseFile(tokenRoot);
            FileLease? fileLease = FileLease.TryAcquireExclusive(leasePath);
            if (fileLease is null)
            {
                entry.Gate.Release();
                Return(key, entry);
                return null;
            }

            return new EvictionLease(this, key, entry, fileLease);
        }
        catch
        {
            entry.Gate.Release();
            Return(key, entry);
            throw;
        }
    }

    private Entry Rent(LeaseKey key)
    {
        while (true)
        {
            Entry entry = _entries.GetOrAdd(key, static _ => new Entry());
            lock (entry.Sync)
            {
                if (entry.Retired)
                    continue;
                entry.References++;
                return entry;
            }
        }
    }

    private void Return(LeaseKey key, Entry entry)
    {
        lock (entry.Sync)
        {
            entry.References--;
            if (entry.References != 0)
                return;

            entry.Retired = true;
            ((ICollection<KeyValuePair<LeaseKey, Entry>>)_entries)
                .Remove(new KeyValuePair<LeaseKey, Entry>(key, entry));
        }
        entry.Gate.Dispose();
    }

    internal bool ReleaseHandoff(string leaseId)
    {
        if (string.IsNullOrEmpty(leaseId) ||
            !_handoffs.TryRemove(leaseId, out HandoffLease? handoff))
            return false;
        handoff.Dispose();
        return true;
    }

    private string RegisterHandoff(LeaseKey key, FileLease fileLease)
    {
        fileLease.DowngradeToShared();
        _handoffCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
        string leaseId;
        try
        {
            do
            {
                leaseId = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(24));
            }
            while (!_handoffs.TryAdd(
                       leaseId, new HandoffLease(this, key, fileLease)));
        }
        catch
        {
            DecrementHandoff(key);
            throw;
        }

        _ = ExpireHandoffAsync(leaseId);
        return leaseId;
    }

    private void DecrementHandoff(LeaseKey key)
    {
        while (_handoffCounts.TryGetValue(key, out int count))
        {
            if (count == 1)
            {
                if (((ICollection<KeyValuePair<LeaseKey, int>>)_handoffCounts)
                    .Remove(new KeyValuePair<LeaseKey, int>(key, count)))
                    return;
            }
            else if (_handoffCounts.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    private async Task ExpireHandoffAsync(string leaseId)
    {
        await Task.Delay(HandoffTimeout);
        ReleaseHandoff(leaseId);
    }

    internal readonly record struct LeaseKey(
        string ShareScope, string GmtToken);

    internal sealed class Entry
    {
        internal readonly object Sync = new();
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
        internal bool Retired;
    }

    internal sealed class MaterializationLease : IDisposable
    {
        private SnapshotCacheLeaseManager? _owner;
        private readonly LeaseKey _key;
        private readonly Entry _entry;
        private FileLease? _fileLease;

        internal MaterializationLease(
            SnapshotCacheLeaseManager owner,
            LeaseKey key,
            Entry entry,
            FileLease fileLease)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
            _fileLease = fileLease;
        }

        internal string PublishHandoff()
        {
            SnapshotCacheLeaseManager? owner =
                Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                throw new ObjectDisposedException(nameof(MaterializationLease));

            FileLease fileLease = _fileLease
                ?? throw new ObjectDisposedException(nameof(MaterializationLease));
            _fileLease = null;
            string leaseId;
            try
            {
                leaseId = owner.RegisterHandoff(_key, fileLease);
            }
            catch
            {
                fileLease.Dispose();
                _entry.Gate.Release();
                owner.Return(_key, _entry);
                throw;
            }

            _entry.Gate.Release();
            owner.Return(_key, _entry);
            return leaseId;
        }

        public void Dispose()
        {
            SnapshotCacheLeaseManager? owner =
                Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;

            _fileLease?.Dispose();
            _fileLease = null;
            _entry.Gate.Release();
            owner.Return(_key, _entry);
        }
    }

    internal sealed class EvictionLease : IDisposable
    {
        private SnapshotCacheLeaseManager? _owner;
        private readonly LeaseKey _key;
        private readonly Entry _entry;
        private FileLease? _fileLease;

        internal EvictionLease(
            SnapshotCacheLeaseManager owner,
            LeaseKey key,
            Entry entry,
            FileLease fileLease)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
            _fileLease = fileLease;
        }

        public void Dispose()
        {
            SnapshotCacheLeaseManager? owner =
                Interlocked.Exchange(ref _owner, null);
            if (owner is null)
                return;
            _fileLease?.Dispose();
            _fileLease = null;
            _entry.Gate.Release();
            owner.Return(_key, _entry);
        }
    }

    private sealed class HandoffLease(
        SnapshotCacheLeaseManager owner,
        LeaseKey key,
        FileLease fileLease) : IDisposable
    {
        private FileLease? _fileLease = fileLease;

        public void Dispose()
        {
            FileLease? lease = Interlocked.Exchange(ref _fileLease, null);
            if (lease is null)
                return;
            lease.Dispose();
            owner.DecrementHandoff(key);
        }
    }

    internal sealed class FileLease : IDisposable
    {
        private const int LockShared = 1;
        private const int LockExclusive = 2;
        private const int LockNonBlocking = 4;
        private const int LockUnlock = 8;

        private FileStream? _stream;

        private FileLease(FileStream stream)
        {
            _stream = stream;
        }

        internal static async ValueTask<FileLease> AcquireExclusiveAsync(
            string path,
            CancellationToken cancellationToken)
            => await AcquireAsync(path, LockExclusive, cancellationToken);

        internal static async ValueTask<FileLease> AcquireSharedAsync(
            string path,
            CancellationToken cancellationToken)
            => await AcquireAsync(path, LockShared, cancellationToken);

        private static async ValueTask<FileLease> AcquireAsync(
            string path,
            int lockKind,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                FileStream stream = Open(path);
                if (TryFlock(stream, lockKind | LockNonBlocking))
                    return new FileLease(stream);
                int error = Marshal.GetLastPInvokeError();
                stream.Dispose();

                if (!OperatingSystem.IsWindows() && error is not 4 and not 11)
                    throw new IOException(
                        $"Failed to acquire snapshot lease (errno {error}).");
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }
        }

        internal static FileLease? TryAcquireExclusive(string path)
        {
            if (!File.Exists(path))
                return null;

            FileStream stream;
            try
            {
                stream = Open(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            if (TryFlock(stream, LockExclusive | LockNonBlocking))
                return new FileLease(stream);

            stream.Dispose();
            return null;
        }

        public void Dispose()
        {
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
                return;
            if (!OperatingSystem.IsWindows())
                _ = Flock(Descriptor(stream), LockUnlock);
            stream.Dispose();
        }

        internal void DowngradeToShared()
        {
            FileStream stream = _stream
                ?? throw new ObjectDisposedException(nameof(FileLease));
            if (!OperatingSystem.IsWindows() &&
                Flock(Descriptor(stream), LockShared) != 0)
                throw new IOException(
                    $"Failed to downgrade snapshot lease (errno {Marshal.GetLastPInvokeError()}).");
        }

        private static FileStream Open(string path) =>
            new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.None);

        private static bool TryFlock(FileStream stream, int operation) =>
            OperatingSystem.IsWindows() ||
            Flock(Descriptor(stream), operation) == 0;

        private static int Descriptor(FileStream stream) =>
            stream.SafeFileHandle.DangerousGetHandle().ToInt32();

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int Flock(int fd, int operation);
    }
}
