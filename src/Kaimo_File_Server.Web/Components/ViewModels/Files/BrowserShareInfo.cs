namespace Kaimo_File_Server.Web.Components.ViewModels;

public enum BrowserShareKind
{
    Local,
    Remote
}

/// <summary>
/// Backend-neutral identity shown by the shared browser. Remote view models do not
/// need to manufacture a local <c>ShareDefinition</c> with a fake filesystem path.
/// </summary>
public sealed record BrowserShareInfo(
    Guid Id,
    string Name,
    BrowserShareKind Kind,
    string? ProviderId = null)
{
    /// <summary>Same share when id and backend kind match; display fields are ignored.</summary>
    public bool IsSameShareAs(BrowserShareInfo? other)
        => other is not null && Id == other.Id && Kind == other.Kind;
}

