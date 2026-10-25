using Xunit;
using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Backup;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Infrastructure.Backup;
using Kaimo_File_Server.Infrastructure.Backup.Restic;
using Kaimo_File_Server.Infrastructure.ExternalStorage;
using Microsoft.Extensions.Configuration;

namespace Kaimo_File_Server.Tests;

public partial class ResticBackupTests
{
    // ─────────────── Backend mapping ───────────────

    private static readonly string LocalRoot = Path.Combine(Path.GetTempPath(), "kaimo-restic-roots");

    [Fact]
    public void Local_BuildsPathUrl_AndPasswordOnlyInEnvironment()
    {
        var settings = new ResticRepositorySettings { Path = Path.Combine(LocalRoot, "repo1") };
        ResticBackend.Validate(BackupBackend.Local, settings, new ResticBackendSecrets(), [LocalRoot]);
        var url = ResticBackend.BuildUrl(BackupBackend.Local, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.Local, settings, new ResticBackendSecrets(), "pw-secret", url);

        Assert.Equal(Path.Combine(LocalRoot, "repo1"), url);
        Assert.Equal("pw-secret", env["RESTIC_PASSWORD"]);
        Assert.Equal(url, env["RESTIC_REPOSITORY"]);
    }

    [Fact]
    public void Local_RejectsPathsOutsideTheRootsOrWithTraversal()
    {
        var outside = new ResticRepositorySettings { Path = Path.Combine(Path.GetTempPath(), "elsewhere", "repo") };
        Assert.Equal("path_outside_roots", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, outside, new(), [LocalRoot])).Code);

        var traversal = new ResticRepositorySettings { Path = LocalRoot + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "x" };
        Assert.Equal("path_invalid", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, traversal, new(), [LocalRoot])).Code);

        // The root itself is not a repository location.
        Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Local, new ResticRepositorySettings { Path = LocalRoot }, new(), [LocalRoot]));
    }

    [Fact]
    public void Rest_KeepsCredentialsOutOfTheUrl()
    {
        var settings = new ResticRepositorySettings { Endpoint = "https://backup.example.com:8000/", Path = "kaimo" };
        var secrets = new ResticBackendSecrets { RestUsername = "kaimo", RestPassword = "rest-secret" };
        ResticBackend.Validate(BackupBackend.Rest, settings, secrets, []);
        var url = ResticBackend.BuildUrl(BackupBackend.Rest, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.Rest, settings, secrets, "pw", url);

        Assert.Equal("rest:https://backup.example.com:8000/kaimo", url);
        Assert.DoesNotContain("rest-secret", url);
        Assert.Equal("rest-secret", env["RESTIC_REST_PASSWORD"]);
    }

    [Theory]
    [InlineData("https://user:pw@backup.example.com")]
    [InlineData("ftp://backup.example.com")]
    [InlineData("https://backup.example.com/?x=1")]
    [InlineData("not a url")]
    public void Rest_RejectsUnsafeEndpoints(string endpoint)
        => Assert.Equal("endpoint_invalid", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Rest, new ResticRepositorySettings { Endpoint = endpoint }, new(), [])).Code);

    [Fact]
    public void S3_BuildsBucketUrl_AndRequiresKeys()
    {
        var settings = new ResticRepositorySettings { Endpoint = "https://s3.example.com", Bucket = "kaimo-backup", Path = "site-a", Region = "eu-central-1" };
        var secrets = new ResticBackendSecrets { S3AccessKeyId = "AK", S3SecretAccessKey = "SK" };
        ResticBackend.Validate(BackupBackend.S3, settings, secrets, []);
        var url = ResticBackend.BuildUrl(BackupBackend.S3, settings);
        var env = ResticBackend.BuildEnvironment(BackupBackend.S3, settings, secrets, "pw", url);

        Assert.Equal("s3:https://s3.example.com/kaimo-backup/site-a", url);
        Assert.Equal("AK", env["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("SK", env["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("eu-central-1", env["AWS_DEFAULT_REGION"]);
        Assert.Equal("credentials_required", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.S3, settings, new ResticBackendSecrets(), [])).Code);
    }

    [Fact]
    public void Sftp_UsesThePinnedSshCommand()
    {
        var ssh = new RsyncSshConnectionSettings("nas.example.com", 2222, "backup", "/volume1", "SHA256:abcdefghijklmnopqrstuvwxyz",
            "/data/kaimo-system/.kaimo-restic-tmp/x/id_key", "/data/kaimo-system/known_hosts");
        var command = ResticBackend.BuildSftpCommand(ssh);
        var url = ResticBackend.BuildUrl(BackupBackend.Sftp, new ResticRepositorySettings { Path = "kaimo" }, ssh);

        Assert.Contains("StrictHostKeyChecking=yes", command);
        Assert.Contains("UserKnownHostsFile=/data/kaimo-system/known_hosts", command);
        Assert.EndsWith("backup@nas.example.com -s sftp", command);
        Assert.Equal("sftp:backup@nas.example.com:/volume1/kaimo", url);
    }

    [Fact]
    public void Sftp_PasswordMode_UsesPinnedSshWithoutKey()
    {
        var ssh = new RsyncSshConnectionSettings("nas.example.com", 2222, "backup", "/volume1", "SHA256:abcdefghijklmnopqrstuvwxyz",
            null, "/data/kaimo-system/known_hosts");
        var command = ResticBackend.BuildSftpCommand(ssh, password: true);

        // Host pinning stays identical to the key mode; only the authentication differs.
        Assert.Contains("StrictHostKeyChecking=yes", command);
        Assert.Contains("UserKnownHostsFile=/data/kaimo-system/known_hosts", command);
        Assert.Contains("PubkeyAuthentication=no", command);
        Assert.Contains("NumberOfPasswordPrompts=1", command);
        // restic splits "-o" values at commas (string slice), which would cut the command apart.
        Assert.DoesNotContain(",", command);
        Assert.DoesNotContain(" -i ", command);
        Assert.DoesNotContain("BatchMode=yes", command);
        Assert.EndsWith("-p 2222 backup@nas.example.com -s sftp", command);
    }

    [Fact]
    public void Sftp_PasswordMode_RejectsShellUnsafeKnownHostsPath()
    {
        var ssh = new RsyncSshConnectionSettings("nas.example.com", 22, "backup", "/volume1", "SHA256:abcdefghijklmnopqrstuvwxyz",
            null, "/data/known hosts;rm -rf /");
        Assert.Throws<ProtocolConfigurationException>(() => ResticBackend.BuildSftpCommand(ssh, password: true));
    }

    [Fact]
    public void Mirror_BetweenSftpPasswordRepositories_SharesOneAskpassHelper()
    {
        ResticTarget Target(string url, string dir) => new(url,
            new Dictionary<string, string>
            {
                ["RESTIC_REPOSITORY"] = url, ["RESTIC_PASSWORD"] = "pw",
                ["SSH_ASKPASS"] = $"/tmp/{dir}/askpass", ["SSH_ASKPASS_REQUIRE"] = "force",
            },
            ["-o", "sftp.command=ssh x"], null);

        // Both helpers of one connection print the same password, so the destination's serves both.
        var copy = ResticClient.BuildCopyTarget(Target("sftp:a", "d1"), Target("sftp:b", "d2"));

        Assert.Equal("/tmp/d1/askpass", copy.Environment["SSH_ASKPASS"]);
        Assert.Equal("sftp:b", copy.Environment["RESTIC_FROM_REPOSITORY"]);
    }

    private const string GcsKey = """{ "type": "service_account", "project_id": "kaimo-backup-1", "private_key": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n" }""";

    // Restic URL syntax verified against restic 0.19.1 (b2:/azure:/gs:/swift:/rclone:).
    [Theory]
    [InlineData(BackupBackend.B2, "b2:kaimo-b2:/site-a")]
    [InlineData(BackupBackend.Azure, "azure:kaimo:/site-a")]
    [InlineData(BackupBackend.Gcs, "gs:kaimo-gcs:/site-a")]
    [InlineData(BackupBackend.Swift, "swift:kaimo:/site-a")]
    public void CloudBackends_BuildUrls_AndKeepSecretsInTheEnvironment(BackupBackend backend, string expectedUrl)
    {
        var settings = new ResticRepositorySettings
        {
            Bucket = expectedUrl.Split(':')[1], Path = "site-a", AzureAccountName = "kaimostore", AzureEndpointSuffix = "core.chinacloudapi.cn",
        };
        var secrets = new ResticBackendSecrets
        {
            B2AccountId = "key-id", B2AccountKey = "b2-secret", AzureSasToken = "?sv=2024&sig=azure-secret", GcsCredentialsJson = GcsKey,
            SwiftEnvironment = "export OS_AUTH_URL=https://keystone:5000/v3\nexport OS_PASSWORD='swift-secret'\necho \"Please enter\"\n# comment",
        };
        ResticBackend.Validate(backend, settings, secrets, []);
        var url = ResticBackend.BuildUrl(backend, settings);
        var env = ResticBackend.BuildEnvironment(backend, settings, secrets, "pw", url);

        Assert.Equal(expectedUrl, url);
        Assert.DoesNotContain("secret", url);
        switch (backend)
        {
            case BackupBackend.B2:
                Assert.Equal("b2-secret", env["B2_ACCOUNT_KEY"]);
                break;
            case BackupBackend.Azure:
                Assert.Equal("sv=2024&sig=azure-secret", env["AZURE_ACCOUNT_SAS"]);
                Assert.False(env.ContainsKey("AZURE_ACCOUNT_KEY"));
                Assert.Equal("core.chinacloudapi.cn", env["AZURE_ENDPOINT_SUFFIX"]);
                break;
            case BackupBackend.Gcs:
                // The key itself goes into a temp file (resolver); only the project id is an env value.
                Assert.Equal("kaimo-backup-1", env["GOOGLE_PROJECT_ID"]);
                break;
            case BackupBackend.Swift:
                Assert.Equal("swift-secret", env["OS_PASSWORD"]);
                Assert.Equal("https://keystone:5000/v3", env["OS_AUTH_URL"]);
                break;
        }
    }

    [Fact]
    public void CloudBackends_RejectInvalidInput()
    {
        string Code(BackupBackend backend, ResticRepositorySettings settings, ResticBackendSecrets secrets)
            => Assert.Throws<ResticException>(() => ResticBackend.Validate(backend, settings, secrets, [])).Code;

        var container = new ResticRepositorySettings { Bucket = "kaimo", AzureAccountName = "kaimostore" };
        Assert.Equal("bucket_invalid", Code(BackupBackend.B2, new ResticRepositorySettings { Bucket = "a:b" }, new() { B2AccountId = "i", B2AccountKey = "k" }));
        Assert.Equal("credentials_required", Code(BackupBackend.B2, container, new()));
        Assert.Equal("azure_credentials_invalid", Code(BackupBackend.Azure, container, new() { AzureAccountKey = "k", AzureSasToken = "s" }));
        Assert.Equal("azure_account_invalid", Code(BackupBackend.Azure, new ResticRepositorySettings { Bucket = "kaimo", AzureAccountName = "Bad_Name" }, new() { AzureAccountKey = "k" }));
        // External-account keys can make the Google client read files or run programs.
        Assert.Equal("gcs_credentials_invalid", Code(BackupBackend.Gcs, container,
            new() { GcsCredentialsJson = """{ "type": "external_account", "credential_source": { "file": "/etc/passwd" } }""" }));
        Assert.Equal("gcs_credentials_invalid", Code(BackupBackend.Gcs, container, new() { GcsCredentialsJson = "not json" }));
        Assert.Equal("swift_variable_invalid", Code(BackupBackend.Swift, container, new() { SwiftEnvironment = "OS_AUTH_URL=x\nLD_PRELOAD=/tmp/x.so" }));
        Assert.Equal("swift_variable_reference", Code(BackupBackend.Swift, container, new() { SwiftEnvironment = "OS_AUTH_URL=x\nexport OS_PASSWORD=$OS_PASSWORD_INPUT" }));
        Assert.Equal("swift_auth_missing", Code(BackupBackend.Swift, container, new() { SwiftEnvironment = "OS_USERNAME=u" }));
    }

    [Fact]
    public void Rclone_NormalizesTheSectionName_AndBlocksLocalAccess()
    {
        var settings = new ResticRepositorySettings { Path = "bucket/kaimo" };
        var secrets = new ResticBackendSecrets { RcloneConfig = "# my remote\n[my-onedrive]\ntype = onedrive\r\ntoken = {\"access_token\":\"t\"}\ndrive_id = abc\n" };
        ResticBackend.Validate(BackupBackend.Rclone, settings, secrets, []);
        var remote = ResticBackend.ParseRcloneConfig(secrets.RcloneConfig);

        Assert.Equal("rclone:kaimo:bucket/kaimo", ResticBackend.BuildUrl(BackupBackend.Rclone, settings));
        Assert.Equal("onedrive", remote.Type);
        Assert.Equal("[kaimo]\ntype = onedrive\ntoken = {\"access_token\":\"t\"}\ndrive_id = abc\n", remote.Config);

        string Code(string config) => Assert.Throws<ResticException>(() => ResticBackend.ParseRcloneConfig(config)).Code;
        Assert.Equal("rclone_type_blocked", Code("[x]\ntype = local"));
        Assert.Equal("rclone_type_blocked", Code("type = alias\nremote = /etc"));
        Assert.Equal("rclone_option_blocked", Code("type = sftp\nhost = h\nssh = sh -c 'id'"));
        Assert.Equal("rclone_option_blocked", Code("type = webdav\nbearer_token_command = cat /etc/shadow"));
        Assert.Equal("rclone_option_blocked", Code("type = drive\nservice_account_file = /data/kaimo-system/key.json"));
        Assert.Equal("rclone_option_blocked", Code("type = azureblob\nclient_certificate_path = /x.pem"));
        Assert.Equal("rclone_config_invalid", Code("[a]\ntype = s3\n[b]\ntype = s3"));
        Assert.Equal("rclone_config_invalid", Code("[a]\nprovider = AWS"));
        Assert.Equal("rclone_config_invalid", Code("type = s3\njust text"));
        Assert.Equal("path_invalid", Assert.Throws<ResticException>(() =>
            ResticBackend.Validate(BackupBackend.Rclone, new ResticRepositorySettings(), secrets, [])).Code);
    }

    [Fact]
    public void ValidateMirror_NewBackendsNeedTheSameAccess()
    {
        var container = new ResticRepositorySettings { Bucket = "a", AzureAccountName = "kaimostore" };
        var rcloneA = new ResticBackendSecrets { RcloneConfig = "[one]\ntype = s3\naccess_key_id = a" };
        var rcloneSameBody = new ResticBackendSecrets { RcloneConfig = "[other-name]\ntype = s3\naccess_key_id = a\n" };
        var rcloneB = new ResticBackendSecrets { RcloneConfig = "[one]\ntype = s3\naccess_key_id = b" };

        ResticBackend.ValidateMirror(Repo(BackupBackend.Rclone, container), rcloneA, Repo(BackupBackend.Rclone, container), rcloneSameBody);
        Assert.Equal("mirror_conflict", Assert.Throws<ResticException>(() =>
            ResticBackend.ValidateMirror(Repo(BackupBackend.Rclone, container), rcloneA, Repo(BackupBackend.Rclone, container), rcloneB)).Code);
        Assert.Equal("mirror_conflict", Assert.Throws<ResticException>(() =>
            ResticBackend.ValidateMirror(Repo(BackupBackend.Azure, container), new() { AzureAccountKey = "x" },
                Repo(BackupBackend.Azure, container), new() { AzureAccountKey = "y" })).Code);
        ResticBackend.ValidateMirror(Repo(BackupBackend.Swift, container), new() { SwiftEnvironment = "OS_AUTH_URL=u\nOS_PASSWORD=p" },
            Repo(BackupBackend.Swift, container), new() { SwiftEnvironment = "export OS_PASSWORD=\"p\"\nexport OS_AUTH_URL=u" });
    }

    [Fact]
    public void CopyTarget_FileEnvironmentOfIdenticalCredentialsDoesNotConflict()
    {
        var a = new ResticTarget("gs:a:/", new Dictionary<string, string> { ["RESTIC_REPOSITORY"] = "gs:a:/", ["RESTIC_PASSWORD"] = "p1", ["GOOGLE_APPLICATION_CREDENTIALS"] = "/tmp/a/gcs-key.json" }, [], null);
        var b = new ResticTarget("gs:b:/", new Dictionary<string, string> { ["RESTIC_REPOSITORY"] = "gs:b:/", ["RESTIC_PASSWORD"] = "p2", ["GOOGLE_APPLICATION_CREDENTIALS"] = "/tmp/b/gcs-key.json" }, [], null);

        var merged = ResticClient.BuildCopyTarget(a, b);

        Assert.Equal("/tmp/a/gcs-key.json", merged.Environment["GOOGLE_APPLICATION_CREDENTIALS"]);
        Assert.Equal("gs:b:/", merged.Environment["RESTIC_FROM_REPOSITORY"]);
    }

    [Fact]
    public void KitCode_IsSixBase32Characters_AndMatchesLeniently()
    {
        var password = ResticBackend.GeneratePassword();
        var code = ResticBackend.KitCode(password);

        Assert.Matches("^[A-Z2-7]{6}$", code);
        Assert.True(ResticBackend.KitCodeMatches(password, " " + code.ToLowerInvariant()[..3] + "-" + code[3..] + " "));
        var wrong = (code[0] == 'A' ? 'B' : 'A') + code[1..];
        Assert.False(ResticBackend.KitCodeMatches(password, wrong));
        Assert.False(ResticBackend.KitCodeMatches(password, ""));
        Assert.NotEqual(ResticBackend.GeneratePassword(), ResticBackend.GeneratePassword());
    }
}
