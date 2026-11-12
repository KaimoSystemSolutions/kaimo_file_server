using Kaimo_File_Server.Core.Storage;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// A user write targets a path reserved by a virtual-share mount point. Derives from
/// <see cref="UnauthorizedAccessException"/> so transports answer exactly like an ACL deny and
/// do not reveal that a (possibly hidden) mount exists.
/// </summary>
public sealed class MountPathReservedException(string relativePath)
    : UnauthorizedAccessException($"Share path '{relativePath}' is reserved.");

public sealed record FileOpenResult(IFileSession Session, FileOpenStatus Status);
