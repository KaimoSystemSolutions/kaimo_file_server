using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Services.File;

namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>
/// Pin of a local share: its file service and definition. <paramref name="UndoVisit"/>
/// identifies the folder visit whose undo history the batch records into; once the user has
/// left it (even to come back later), the batch's remaining steps are no longer recorded.
/// </summary>
public sealed record LocalFileOperationTarget(IFileService Service, ShareDefinition Share, int UndoVisit)
    : FileOperationTarget;
