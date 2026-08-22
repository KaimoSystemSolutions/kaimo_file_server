using Microsoft.AspNetCore.Components;
using Kaimo_File_Server.Web.Services;

namespace Kaimo_File_Server.Web.Components;

/// <summary>
/// Base component that exposes the application's <see cref="AssetProvider"/> as <c>Assets</c>.
/// .NET 10 added <see cref="ComponentBase.Assets"/> for fingerprinted static assets; this project
/// uses its own inline-SVG provider under the same name, so we hide the base member with <c>new</c>
/// to make the intent explicit and silence CS0108.
/// </summary>
public abstract class AppComponentBase : ComponentBase
{
    [Inject]
    public new AssetProvider Assets { get; set; } = default!;
}

/// <summary>
/// Layout counterpart of <see cref="AppComponentBase"/> for components deriving from
/// <see cref="LayoutComponentBase"/>. See that type for why <c>Assets</c> is hidden with <c>new</c>.
/// </summary>
public abstract class AppLayoutComponentBase : LayoutComponentBase
{
    [Inject]
    public new AssetProvider Assets { get; set; } = default!;
}
