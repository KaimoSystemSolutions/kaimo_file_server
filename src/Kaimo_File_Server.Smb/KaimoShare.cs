using Smb.FileSystem;

namespace Kaimo_File_Server.Smb;

/// <summary>
/// A Kaimo SMB share. Implements the library's <see cref="IShare"/> and additionally carries the
/// database <see cref="ShareId"/> and the <see cref="IsHidden"/> flag so <see cref="KaimoSharePolicy"/>
/// can run the ACL/ABE checks (which key off the share GUID).
/// </summary>
internal sealed class KaimoShare : IShare
{
    public required string Name { get; init; }
    public ShareType Type { get; init; } = ShareType.Disk;
    public IFileStore? FileStore { get; init; }
    public bool EncryptData { get; init; }
    public string Remark { get; init; } = string.Empty;

    /// <summary>Database id of the share (used by the authorization policy).</summary>
    public required Guid ShareId { get; init; }

    /// <summary>Hidden from enumeration (ABE) but reachable via its direct path.</summary>
    public bool IsHidden { get; init; }
}
