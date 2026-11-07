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

    // Uniquely named so it never collides with a derived component's own injected logger.
    [Inject]
    private ILoggerFactory RefreshLoggerFactory { get; set; } = default!;

    /// <summary>
    /// Runs <paramref name="refresh"/> without blocking the caller and re-renders when it reports a
    /// change (stale-while-revalidate, e.g. a cached principal list). The refresh handles its own
    /// load errors. A disposed component or ended circuit is ignored; any other failure is a bug
    /// and is logged, since nobody observes this fire-and-forget task.
    /// </summary>
    protected void RefreshInBackground(Func<Task<bool>> refresh) => _ = RefreshAsync(refresh);

    private async Task RefreshAsync(Func<Task<bool>> refresh)
    {
        try
        {
            if (await refresh())
                await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // Disposed component or ended circuit: nothing left to update.
        }
        catch (Exception ex)
        {
            RefreshLoggerFactory.CreateLogger(GetType())
                .LogError(ex, "Background refresh of {Component} failed", GetType().Name);
        }
    }
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
