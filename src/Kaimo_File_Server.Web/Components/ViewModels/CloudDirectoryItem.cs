namespace Kaimo_File_Server.Web.Components.ViewModels;

/// <summary>Minimal directory-picker item shared by local and remote sources.</summary>
public sealed record CloudDirectoryItem(string Name, string Path);
