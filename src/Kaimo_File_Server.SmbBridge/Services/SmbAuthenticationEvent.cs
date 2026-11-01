namespace Kaimo_File_Server.SmbBridge.Services;

/// <summary>One SMB login as reported by the Samba container: when, which account, from where, Samba's NT status.</summary>
public sealed record SmbAuthenticationEvent(DateTime AtUtc, string Username, string? Address, string Status);
