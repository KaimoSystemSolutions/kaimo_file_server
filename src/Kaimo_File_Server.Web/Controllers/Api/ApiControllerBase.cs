using System.Security.Claims;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Kaimo_File_Server.Web.Controllers.Api;

/// <summary>Uniform machine-readable error envelope for the client API.</summary>
/// <param name="Code">Stable, non-localized machine code (e.g. "not_found", "forbidden").</param>
/// <param name="Message">Short human-readable hint (English; for developers, not end users).</param>
public sealed record ApiError(string Code, string Message);

/// <summary>
/// Base class for all <c>/api/v1</c> controllers. Rebuilds the caller's
/// <see cref="UserContext"/> from the validated JWT on every request (never
/// trusting role claims for authorization) and offers helpers for the uniform
/// error envelope. Concrete controllers stay thin and delegate to core services.
/// </summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Resolves the authenticated user's full <see cref="UserContext"/> from the
    /// token's subject id, re-checking existence and the enabled flag against the
    /// database. Returns <c>null</c> when the account is gone or disabled — the
    /// caller should surface that as <see cref="Unauthorized"/>.
    /// </summary>
    protected async Task<UserContext?> ResolveUserAsync(IUserContextFactory factory)
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var userId))
            return null;

        var context = await factory.CreateByUserIdAsync(userId);
        return context is { User.IsEnabled: true } ? context : null;
    }

    /// <summary>401 with a machine code.</summary>
    protected IActionResult ApiUnauthorized(string message = "Authentication required.")
        => Unauthorized(new ApiError("unauthorized", message));

    /// <summary>403 with a machine code.</summary>
    protected IActionResult ApiForbidden(string message = "Access denied.")
        => StatusCode(StatusCodes.Status403Forbidden, new ApiError("forbidden", message));

    /// <summary>404 with a machine code.</summary>
    protected IActionResult ApiNotFound(string message = "Resource not found.")
        => NotFound(new ApiError("not_found", message));

    /// <summary>400 with a machine code.</summary>
    protected IActionResult ApiBadRequest(string code, string message)
        => BadRequest(new ApiError(code, message));
}
