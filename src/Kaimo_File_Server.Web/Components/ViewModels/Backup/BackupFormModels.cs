using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Infrastructure.Backup.Restic;

namespace Kaimo_File_Server.Web.Components.ViewModels.Backup;

/// <summary>Editable state of the repository form.</summary>
public sealed class BackupRepositoryForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public BackupBackend Backend { get; set; } = BackupBackend.Local;
    public string Path { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool S3PathStyle { get; set; }
    public Guid ConnectionId { get; set; }
    public string CaCertificatePem { get; set; } = string.Empty;
    public string RestUsername { get; set; } = string.Empty;
    public string RestPassword { get; set; } = string.Empty;
    public string S3AccessKeyId { get; set; } = string.Empty;
    public string S3SecretAccessKey { get; set; } = string.Empty;
    public string AzureAccountName { get; set; } = string.Empty;
    public string AzureEndpointSuffix { get; set; } = string.Empty;
    public string B2AccountId { get; set; } = string.Empty;
    public string B2AccountKey { get; set; } = string.Empty;
    public string AzureAccountKey { get; set; } = string.Empty;
    public string AzureSasToken { get; set; } = string.Empty;
    public string GcsCredentialsJson { get; set; } = string.Empty;
    public string SwiftEnvironment { get; set; } = string.Empty;
    public string RcloneConfig { get; set; } = string.Empty;
    public bool IsAppendOnly { get; set; }
    public int MinFreeSpaceGb { get; set; } = 5;
    public List<CheckboxItem<Department>> Departments { get; set; } = [];
    public List<TimeOnly> MaintenanceTimes { get; set; } = [.. BackupSchedule.DefaultMaintenance().Times];
    public HashSet<DayOfWeek> MaintenanceDays { get; set; } = [.. BackupSchedule.DefaultMaintenance().Days];
    public bool ReadDataMonthly { get; set; } = true;
    public int TestRestoreSampleSize { get; set; } = 20;
    public Guid MirrorTargetId { get; set; }

    /// <summary>Password typed for "connect existing"; never stored in the form after use.</summary>
    public string ExistingPassword { get; set; } = string.Empty;

    public BackupRepositoryDraft ToDraft() => new(
        Name,
        Backend,
        new ResticRepositorySettings
        {
            Path = Blank(Path),
            Endpoint = Backend is BackupBackend.Rest or BackupBackend.S3 ? Blank(Endpoint) : null,
            Bucket = Backend is BackupBackend.S3 or BackupBackend.B2 or BackupBackend.Azure or BackupBackend.Gcs or BackupBackend.Swift ? Blank(Bucket) : null,
            Region = Backend == BackupBackend.S3 ? Blank(Region) : null,
            S3PathStyle = Backend == BackupBackend.S3 && S3PathStyle,
            StorageConnectionId = Backend is BackupBackend.Sftp or BackupBackend.Smb && ConnectionId != Guid.Empty ? ConnectionId : null,
            CaCertificatePem = Backend is BackupBackend.Rest or BackupBackend.S3 or BackupBackend.Swift ? Blank(CaCertificatePem) : null,
            AzureAccountName = Backend == BackupBackend.Azure ? Blank(AzureAccountName) : null,
            AzureEndpointSuffix = Backend == BackupBackend.Azure ? Blank(AzureEndpointSuffix) : null,
        },
        new ResticBackendSecrets
        {
            RestUsername = Backend == BackupBackend.Rest ? Blank(RestUsername) : null,
            RestPassword = Backend == BackupBackend.Rest ? Blank(RestPassword) : null,
            S3AccessKeyId = Backend == BackupBackend.S3 ? Blank(S3AccessKeyId) : null,
            S3SecretAccessKey = Backend == BackupBackend.S3 ? Blank(S3SecretAccessKey) : null,
            B2AccountId = Backend == BackupBackend.B2 ? Blank(B2AccountId) : null,
            B2AccountKey = Backend == BackupBackend.B2 ? Blank(B2AccountKey) : null,
            AzureAccountKey = Backend == BackupBackend.Azure ? Blank(AzureAccountKey) : null,
            AzureSasToken = Backend == BackupBackend.Azure ? Blank(AzureSasToken) : null,
            GcsCredentialsJson = Backend == BackupBackend.Gcs ? Blank(GcsCredentialsJson) : null,
            SwiftEnvironment = Backend == BackupBackend.Swift ? Blank(SwiftEnvironment) : null,
            RcloneConfig = Backend == BackupBackend.Rclone ? Blank(RcloneConfig) : null,
        },
        IsAppendOnly,
        MinFreeSpaceGb,
        Departments.Where(d => d.IsChecked).Select(d => d.Item.Id).ToList(),
        new BackupSchedule { Times = MaintenanceTimes.ToList(), Days = MaintenanceDays.ToList() },
        ReadDataMonthly,
        TestRestoreSampleSize,
        MirrorTargetId == Guid.Empty ? null : MirrorTargetId);

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
/// <summary>Editable state of the job form.</summary>
public sealed class BackupJobForm
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IncludeRecycleBin { get; set; }
    public List<CheckboxItem<ShareDefinition>> Shares { get; set; } = [];
    public List<TimeOnly> Times { get; set; } = [new(2, 0)];
    public HashSet<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];
    public int KeepLast { get; set; }
    public int KeepDaily { get; set; }
    public int KeepWeekly { get; set; }
    public int KeepMonthly { get; set; }
    public int KeepYearly { get; set; }
    public int AlertAfterDays { get; set; } = 3;
    public BackupContentLevel ContentLevel { get; set; } = BackupContentLevel.Files;
    public List<CheckboxItem<string>> Pools { get; set; } = [];
    public bool IncludeVersions { get; set; }

    /// <summary>
    /// A job backs up either single shares or whole pools (which contain their shares). The
    /// selection of the other kind is kept while switching but never sent.
    /// </summary>
    public BackupSourceKind SourceKind { get; set; } = BackupSourceKind.Share;

    public IEnumerable<Guid> SelectedShareIds => SourceKind == BackupSourceKind.Share
        ? Shares.Where(s => s.IsChecked).Select(s => s.Item.Id)
        : [];

    public IEnumerable<string> SelectedPools => SourceKind == BackupSourceKind.Pool
        ? Pools.Where(p => p.IsChecked).Select(p => p.Item)
        : [];

    /// <summary>A database or Full job needs no file source; a files job at least one.</summary>
    public bool HasSources => SelectedShareIds.Any() || SelectedPools.Any() || ContentLevel != BackupContentLevel.Files;

    public BackupRetention ToRetention() => new()
    {
        KeepLast = KeepLast, KeepDaily = KeepDaily, KeepWeekly = KeepWeekly, KeepMonthly = KeepMonthly, KeepYearly = KeepYearly,
    };

    public void SetRetention(BackupRetention retention)
    {
        KeepLast = retention.KeepLast;
        KeepDaily = retention.KeepDaily;
        KeepWeekly = retention.KeepWeekly;
        KeepMonthly = retention.KeepMonthly;
        KeepYearly = retention.KeepYearly;
    }

    public BackupJobDraft ToDraft() => new(
        Id, Name, RepositoryId, Enabled, IncludeRecycleBin,
        SelectedShareIds.ToList(), Times.ToList(), Days.ToList(), ToRetention(), AlertAfterDays,
        ContentLevel, SelectedPools.ToList(), IncludeVersions);
}
/// <summary>Editable state of the restore dialog.</summary>
public sealed class BackupRestoreForm
{
    public BackupRestoreMode Mode { get; set; } = BackupRestoreMode.NewFolder;
    public BackupRestoreConflict Conflict { get; set; } = BackupRestoreConflict.Rename;
    public bool RestorePermissions { get; set; }
    public Guid TargetShareId { get; set; }
    public string TargetFolder { get; set; } = string.Empty;
}
