using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kaimo_File_Server.Infrastructure.Notifications;

/// <summary>Transport security of the SMTP connection.</summary>
public enum SmtpSecurity
{
    /// <summary>SSL on connect for port 465, otherwise STARTTLS when the server offers it.</summary>
    Auto = 0,

    /// <summary>Plain text; only for a trusted local relay.</summary>
    None = 1,

    /// <summary>Upgrade with STARTTLS; fails when the server does not offer it.</summary>
    StartTls = 2,

    /// <summary>Implicit TLS from the first byte (usually port 465).</summary>
    SslOnConnect = 3,
}

/// <summary>How the gateway authenticates at the SMTP server.</summary>
public enum SmtpAuthMode
{
    /// <summary>Anonymous relay (e.g. an internal mail server that trusts the host).</summary>
    None = 0,

    /// <summary>User name and password.</summary>
    Password = 1,
}

/// <summary>
/// SMTP gateway configuration, stored in <c>ConfigSettings</c> under <see cref="ConfigKey"/>.
/// The password is kept only in encrypted form (<see cref="EncryptedPassword"/>, Data
/// Protection via <see cref="ICredentialVault"/>), which only the Web process can decrypt.
/// </summary>
public sealed record SmtpSettings
{
    public const string ConfigKey = "notifications.smtp";

    /// <summary>Credential-vault context of <see cref="EncryptedPassword"/>.</summary>
    public static readonly CredentialContext PasswordContext =
        new(WellKnownGUIDs.SMTP_CREDENTIAL, "smtp", "smtp-password", 1);

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public SmtpAuthMode AuthMode { get; set; } = SmtpAuthMode.Password;
    public string Username { get; set; } = string.Empty;

    /// <summary>The password protected by <see cref="ICredentialVault"/>; never plain text.</summary>
    public string? EncryptedPassword { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Kaimo Files";
    public string ReplyTo { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Upper bound of mails the dispatcher sends per minute (flood protection).</summary>
    public int MaxMailsPerMinute { get; set; } = 30;

    /// <summary>Whether enough is configured to attempt sending.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    public void Normalize()
    {
        Host = (Host ?? string.Empty).Trim();
        Username = (Username ?? string.Empty).Trim();
        FromAddress = (FromAddress ?? string.Empty).Trim();
        FromName = (FromName ?? string.Empty).Trim();
        ReplyTo = (ReplyTo ?? string.Empty).Trim();
        Port = Math.Clamp(Port, 1, 65535);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        MaxMailsPerMinute = Math.Clamp(MaxMailsPerMinute, 1, 1000);
        if (!Enum.IsDefined(Security)) Security = SmtpSecurity.Auto;
        if (!Enum.IsDefined(AuthMode)) AuthMode = SmtpAuthMode.Password;
    }

    /// <summary>
    /// Whether the stored password may be used with these settings: only while the server,
    /// port, transport security and user name are unchanged. Otherwise whoever can edit the
    /// settings could send the stored password to a server of their choice (or in plain text).
    /// </summary>
    public bool CanReuseStoredPasswordOf(SmtpSettings stored)
    {
        var draft = this with { };
        draft.Normalize();
        return string.Equals(draft.Host, stored.Host, StringComparison.OrdinalIgnoreCase)
               && draft.Port == stored.Port
               && draft.Security == stored.Security
               && string.Equals(draft.Username, stored.Username, StringComparison.Ordinal);
    }

    public static SmtpSettings Default() => new();
}

/// <summary>Outcome of the most recent SMTP connection (test mail or real send), for the settings page.</summary>
public sealed record SmtpStatus
{
    public const string ConfigKey = "notifications.smtp.status";

    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
}

public interface ISmtpConfigStore
{
    Task<SmtpSettings> GetAsync();
    Task SetAsync(SmtpSettings settings);
    Task<SmtpStatus> GetStatusAsync();

    /// <summary>Records a success (<paramref name="error"/> null) or a failure.</summary>
    Task RecordStatusAsync(string? error);
}

/// <summary>
/// Reads and writes <see cref="SmtpSettings"/>. Always reads fresh, because the settings are
/// consulted rarely (only when mails are due) and must never lag behind a save.
/// </summary>
public sealed class SmtpConfigStore(IServiceScopeFactory scopeFactory) : ISmtpConfigStore
{
    public async Task<SmtpSettings> GetAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var settings = await config.GetFreshAsync(SmtpSettings.ConfigKey, SmtpSettings.Default());
        settings.Normalize();
        return settings;
    }

    public async Task SetAsync(SmtpSettings settings)
    {
        var normalized = settings with { };
        normalized.Normalize();
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
            .SetAsync(SmtpSettings.ConfigKey, normalized);
    }

    public async Task<SmtpStatus> GetStatusAsync()
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
            .GetFreshAsync(SmtpStatus.ConfigKey, new SmtpStatus());
    }

    public async Task RecordStatusAsync(string? error)
    {
        var status = await GetStatusAsync();
        if (error is null) status.LastSuccessUtc = DateTime.UtcNow;
        else { status.LastErrorUtc = DateTime.UtcNow; status.LastError = error; }
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IConfigRepository>().SetAsync(SmtpStatus.ConfigKey, status);
    }
}
