using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services.DataServices;
using Kaimo_File_Server.Infrastructure.Clouds;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Configuration record for a network share (SMB/HTTP).
    ///
    /// The <see cref="Name"/> serves as both the directory name on disk
    /// and the share name advertised to clients. <see cref="Path"/>
    /// points to the physical storage location on the host file system.
    ///
    /// Each share belongs to exactly ONE department via <see cref="DepartmentId"/>.
    /// Shares without explicit assignment default to the Global department.
    ///
    /// Shares can be temporarily disabled (<see cref="IsEnabled"/> = false)
    /// without deleting their configuration, and optionally maintain a
    /// per-share recycle bin (<see cref="IsRecycleEnabled"/>).
    /// </summary>
    public class ShareDefinition
    {
        /// <summary>Unique identifier for this share.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Share name visible to clients and used as the directory name.
        /// Must be unique across all shares.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path to the share's root directory on the host
        /// file system (e.g. "/data/storage/projekte").
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// The department this share belongs to.
        /// Determines which department-scoped administrators can manage this share
        /// and which department default file permissions apply.
        /// Defaults to <see cref="WellKnownGUIDs.DEPARTMENT_GLOBAL"/> (Global department).
        /// A share always belongs to exactly one department.
        /// </summary>
        public Guid DepartmentId { get; set; } = WellKnownGUIDs.DEPARTMENT_GLOBAL;

        /// <summary>
        /// When <c>false</c>, the share is hidden from directory listings
        /// and all access attempts are rejected.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// When <c>true</c>, the share is hidden from directory listings but
        /// still accessible to users with direct proper rights.
        /// </summary>
        public bool IsShareHidden { get; set; }

        /// <summary>
        /// When <c>true</c>, deleted files are moved to a hidden
        /// <c>.recycle</c> directory inside the share instead of
        /// being permanently removed.
        /// </summary>
        public bool IsRecycleEnabled { get; set; }

        public CloudSettings CloudSettings { get; set; } = new CloudSettings(new Dictionary<string, SyncedFolder>());

        public ICloudConnection? CloudConnection { get; set; }

        /// <summary>EF Core / serialization constructor.</summary>
        internal ShareDefinition()
        {
        }

        /// <param name="name">Share / directory name — must not be blank.</param>
        /// <param name="path">Absolute host path — must not be blank.</param>
        /// <param name="departmentId">
        /// Department this share belongs to.
        /// Pass <c>null</c> or omit to default to the Global department.
        /// </param>
        /// <param name="isEnabled">Whether the share is active on creation.</param>
        /// <param name="isRecycleEnabled">Whether the recycle bin is active.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> or <paramref name="path"/> is blank.
        /// </exception>
        public ShareDefinition(
            string name,
            string path,
            Guid? departmentId = null,
            bool isEnabled = true,
            bool isShareHidden = false,
            bool isRecycleEnabled = false,
            string? cloudSettings = null)
        {
            Id = Guid.NewGuid();

            SambaName.EnsureValidShareName(name, nameof(name));
            Name = name;

            Path = !string.IsNullOrWhiteSpace(path)
                ? path
                : throw new ArgumentException("Path must not be empty.", nameof(path));

            DepartmentId = departmentId ?? WellKnownGUIDs.DEPARTMENT_GLOBAL;
            IsShareHidden = isShareHidden;
            IsEnabled = isEnabled;
            IsRecycleEnabled = isRecycleEnabled;
            CloudSettings = cloudSettings is null
                ? new CloudSettings(new Dictionary<string, SyncedFolder>())
                : CloudSettings.Deserialize(cloudSettings);
        }
    }


    public record CloudSettings(
        // path -> synced folder data
        Dictionary<string, SyncedFolder> Folders
    )
    {
        public string Serialize()
            => JsonSerializer.Serialize(this);

        public static CloudSettings Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new CloudSettings(new Dictionary<string, SyncedFolder>());

            return JsonSerializer.Deserialize<CloudSettings>(json)
                   ?? throw new InvalidOperationException("Invalid CloudSettings JSON");
        }
    }

    /// <summary>
    /// Hourly weekly schedule for one cloud-sync mapping. Slots are encoded as
    /// <c>(int)DayOfWeek * 24 + hour</c>, keeping the persisted JSON compact while
    /// still allowing the UI to render a full 7 x 24 grid.
    /// </summary>
    public sealed class CloudSyncSchedule : IEquatable<CloudSyncSchedule>
    {
        public const int HoursPerDay = 24;
        public const int SlotCount = 7 * HoursPerDay;
        public const int DefaultIntervalSeconds = 60;
        public const int MinIntervalSeconds = 1;
        public const int MaxIntervalSeconds = 86_400;

        public bool IsEnabled { get; set; }

        public HashSet<int> ActiveSlots { get; set; } = [];

        /// <summary>How often this individual mapping is evaluated.</summary>
        public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

        /// <summary>
        /// User whose ACL context is used by unattended runs. The value is set
        /// whenever an enabled schedule is saved by an authorized administrator.
        /// </summary>
        public string? RunAsUsername { get; set; }

        public bool IsActive(DayOfWeek day, int hour)
            => ActiveSlots?.Contains(ToSlot(day, hour)) == true;

        public void SetActive(DayOfWeek day, int hour, bool active)
        {
            ActiveSlots ??= [];
            int slot = ToSlot(day, hour);
            if (active)
                ActiveSlots.Add(slot);
            else
                ActiveSlots.Remove(slot);
        }

        public CloudSyncSchedule Clone()
            => new()
            {
                IsEnabled = IsEnabled,
                ActiveSlots = ActiveSlots is null
                    ? []
                    : new HashSet<int>(ActiveSlots.Where(IsValidSlot)),
                IntervalSeconds = GetEffectiveIntervalSeconds(),
                RunAsUsername = RunAsUsername
            };

        public bool HasValidInterval
            => IntervalSeconds is >= MinIntervalSeconds and <= MaxIntervalSeconds;

        public int GetEffectiveIntervalSeconds()
            => HasValidInterval ? IntervalSeconds : DefaultIntervalSeconds;

        public static int ToSlot(DayOfWeek day, int hour)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(hour);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hour, HoursPerDay);
            return (int)day * HoursPerDay + hour;
        }

        public static bool IsValidSlot(int slot)
            => slot >= 0 && slot < SlotCount;

        public bool Equals(CloudSyncSchedule? other)
            => other is not null
               && IsEnabled == other.IsEnabled
               && IntervalSeconds == other.IntervalSeconds
               && string.Equals(RunAsUsername, other.RunAsUsername, StringComparison.Ordinal)
               && (ActiveSlots ?? []).SetEquals(other.ActiveSlots ?? []);

        public override bool Equals(object? obj) => Equals(obj as CloudSyncSchedule);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(IsEnabled);
            hash.Add(IntervalSeconds);
            hash.Add(RunAsUsername, StringComparer.Ordinal);
            foreach (int slot in (ActiveSlots ?? []).Order())
                hash.Add(slot);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A single folder's cloud-sync config. Provider/Data are the persisted part
    /// (round-tripped through CloudSettings' JSON). Connection is a live,
    /// in-memory handle built on demand by ICloudProviderFactory — it is never
    /// serialized and does not survive a reload of the share from the DB.
    /// </summary>
    public class SyncedFolder : IEquatable<SyncedFolder>
    {
        /// <summary>User-facing label for this mapping; it does not affect its paths or behavior.</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Optional user-facing notes for this mapping.</summary>
        public string Description { get; set; } = "";
        public string Provider { get; set; } = "";

        public Dictionary<string, string> Data { get; set; } = new();

        /// <summary>
        /// Path on the remote provider. Defaults to root.
        /// </summary>
        public string RemotePath { get; set; } = "/";

        /// <summary>
        /// Newly authorized mappings require an explicit remote-folder selection
        /// before they can be used. Root is a valid selection when it is chosen
        /// deliberately; legacy mappings retain their existing configuration.
        /// </summary>
        public bool RequiresRemoteFolderSelection { get; set; }

        /// <summary>
        /// Timestamp of the last successful sync.
        /// Null means the folder has never been synced.
        /// </summary>
        public DateTime? LastSync { get; set; }

        public SyncMode Mode { get; set; } = SyncMode.TwoWay;

        /// <summary>Optional hourly weekly timer. Disabled for legacy mappings.</summary>
        public CloudSyncSchedule Schedule { get; set; } = new();

        /// <summary>Optional filters and bandwidth limits for advanced users.</summary>
        public CloudSyncAdvancedSettings AdvancedSettings { get; set; } = new();
        
        public SyncedFolder(
            string provider,
            Dictionary<string, string> data,
            string remotePath = "/",
            DateTime? lastSync = null)
        {
            Provider = provider;
            Data = data;
            RemotePath = remotePath;
            LastSync = lastSync;
        }

        public bool Equals(SyncedFolder? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            if (Provider != other.Provider) return false;
            if (RemotePath != other.RemotePath) return false;
            if (RequiresRemoteFolderSelection != other.RequiresRemoteFolderSelection) return false;
            if (LastSync != other.LastSync) return false;
            if (Mode != other.Mode) return false;
            if (!Equals(Schedule, other.Schedule)) return false;
            if (!string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)) return false;
            if (!string.Equals(Description, other.Description, StringComparison.Ordinal)) return false;
            if (!Equals(AdvancedSettings, other.AdvancedSettings)) return false;
            if (Data.Count != other.Data.Count) return false;

            foreach (var kvp in Data)
            {
                if (!other.Data.TryGetValue(kvp.Key, out var otherValue))
                    return false;

                if (kvp.Value != otherValue)
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as SyncedFolder);

        public override int GetHashCode()
        {
            var hash = new HashCode();

            hash.Add(Provider);
            hash.Add(RemotePath);
            hash.Add(RequiresRemoteFolderSelection);
            hash.Add(LastSync);
            hash.Add(Mode);
            hash.Add(Schedule);
            hash.Add(DisplayName, StringComparer.Ordinal);
            hash.Add(Description, StringComparer.Ordinal);
            hash.Add(AdvancedSettings);

            // Order-independent dictionary hash
            int dataHash = 0;
            foreach (var kvp in Data)
            {
                dataHash ^= HashCode.Combine(kvp.Key, kvp.Value);
            }

            hash.Add(dataHash);

            return hash.ToHashCode();
        }

        public static bool operator ==(SyncedFolder? left, SyncedFolder? right)
            => left is null ? right is null : left.Equals(right);

        public static bool operator !=(SyncedFolder? left, SyncedFolder? right)
            => !(left == right);
    }

    /// <summary>Optional transfer constraints persisted with a cloud-sync mapping.</summary>
    public sealed class CloudSyncAdvancedSettings : IEquatable<CloudSyncAdvancedSettings>
    {
        /// <summary>Maximum size of an individual synced file in bytes; null means unlimited.</summary>
        public long? MaxFileSizeBytes { get; set; }

        /// <summary>File extensions to skip, stored normalized with a leading dot.</summary>
        public HashSet<string> ExcludedExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Maximum upload throughput in bytes/second; null means unlimited.</summary>
        public long? MaxUploadBytesPerSecond { get; set; }

        /// <summary>Maximum download throughput in bytes/second; null means unlimited.</summary>
        public long? MaxDownloadBytesPerSecond { get; set; }

        /// <summary>
        /// When <c>true</c>, a two-way sync propagates deletions instead of
        /// restoring the missing item from the other endpoint. A file or folder
        /// removed on one side is then removed on the other side as well, so both
        /// endpoints converge on the same state. Deletion detection relies on the
        /// snapshot recorded after the previous successful run
        /// (<see cref="SyncDefinitionRuntime.LastSyncManifest"/>): the first run
        /// after enabling this option has no baseline and therefore only copies —
        /// it never mass-deletes. Ignored for push and pull, which have no
        /// ambiguity to resolve.
        /// </summary>
        public bool SyncDeletions { get; set; }

        /// <summary>
        /// When <c>true</c>, per-item access control lists may only be managed on
        /// this sync's root folder — the ACL editor blocks adding or editing
        /// permissions on any file or subfolder below it. Rationale: the
        /// synchronization reconciles by path (no stable file identity), so a
        /// rename or move it performs drops the ACL that was attached to the old
        /// path. Restricting permissions to the stable root avoids that
        /// silent-loss surprise. Existing entries below the root keep working and
        /// remain removable; only new writes below the root are refused.
        /// </summary>
        public bool RootLevelPermissionsOnly { get; set; }

        public CloudSyncAdvancedSettings Clone() => new()
        {
            MaxFileSizeBytes = MaxFileSizeBytes,
            ExcludedExtensions = new HashSet<string>(ExcludedExtensions ?? [], StringComparer.OrdinalIgnoreCase),
            MaxUploadBytesPerSecond = MaxUploadBytesPerSecond,
            MaxDownloadBytesPerSecond = MaxDownloadBytesPerSecond,
            SyncDeletions = SyncDeletions,
            RootLevelPermissionsOnly = RootLevelPermissionsOnly
        };

        public bool Equals(CloudSyncAdvancedSettings? other)
            => other is not null
               && MaxFileSizeBytes == other.MaxFileSizeBytes
               && MaxUploadBytesPerSecond == other.MaxUploadBytesPerSecond
               && MaxDownloadBytesPerSecond == other.MaxDownloadBytesPerSecond
               && SyncDeletions == other.SyncDeletions
               && RootLevelPermissionsOnly == other.RootLevelPermissionsOnly
               && (ExcludedExtensions ?? []).SetEquals(other.ExcludedExtensions ?? []);

        public override bool Equals(object? obj) => Equals(obj as CloudSyncAdvancedSettings);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MaxFileSizeBytes);
            hash.Add(MaxUploadBytesPerSecond);
            hash.Add(MaxDownloadBytesPerSecond);
            hash.Add(SyncDeletions);
            hash.Add(RootLevelPermissionsOnly);
            foreach (var extension in (ExcludedExtensions ?? []).Order(StringComparer.OrdinalIgnoreCase))
                hash.Add(extension, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
