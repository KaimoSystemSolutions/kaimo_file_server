using Kaimo_File_Server.Core.Domain;

namespace Kaimo_File_Server.Core.Services.File;

/// <summary>
/// The row changes of one history rename, planned by <see cref="VersionHistoryMerge"/>. Every
/// change is keyed by row id, so applying the same plan twice (a replay after a lost COMMIT
/// acknowledgement) yields the same rows.
/// </summary>
/// <param name="NewPaths">Moved row id → its new path.</param>
/// <param name="Displaced">Destination rows in the same second as a moved row of the same file.</param>
/// <param name="VersionNumbers">Row id → new version number, for files whose histories were merged.</param>
/// <param name="LiveCopies">New rows that make the moved (now live) content the newest version.</param>
public sealed record VersionRenamePlan(
    IReadOnlyDictionary<Guid, string> NewPaths,
    IReadOnlyList<FileVersion> Displaced,
    IReadOnlyDictionary<Guid, int> VersionNumbers,
    IReadOnlyList<FileVersion> LiveCopies);
