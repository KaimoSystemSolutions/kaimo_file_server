namespace Kaimo_File_Server.Web.Components;

/// <summary>
/// Scoped (per-circuit) channel that lets the sidebar ask the user administration page to
/// jump to the actor's own profile — without routing state through the URL. A request made
/// while the page is already mounted is delivered via <see cref="SelfRequested"/>; one made
/// from another page is held as pending and consumed once the page loads.
/// </summary>
public sealed class ProfileNavigator
{
    private bool _pending;

    /// <summary>Raised when the page is already mounted and should select the actor now.</summary>
    public event Func<Task>? SelfRequested;

    /// <summary>Sidebar entry point: notify a mounted listener, else remember for next load.</summary>
    public async Task RequestSelfAsync()
    {
        if (SelfRequested is not null)
        {
            await SelfRequested.Invoke();
            _pending = false;
        }
        else
        {
            _pending = true;
        }
    }

    /// <summary>Page entry point after load: returns (and clears) any request made earlier.</summary>
    public bool ConsumePending()
    {
        var pending = _pending;
        _pending = false;
        return pending;
    }
}
