using System.Security.Claims;
using System.Text.Json;
using Kaimo_File_Server.Core.Domain.ClientSync;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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

        // Device-scoped tokens (all client-API tokens) are only valid while their
        // device is still active. Re-checking here means an admin revoking a device
        // takes effect on the very next request, not only when the JWT expires.
        if (Guid.TryParse(User.FindFirstValue(JwtTokenService.DeviceIdClaim), out var deviceId))
        {
            var devices = HttpContext.RequestServices.GetRequiredService<ISyncDeviceRepository>();
            var device = await devices.GetByIdAsync(deviceId);
            if (device is null || !device.IsActive)
                return null;
        }

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

    /// <summary>412 with a machine code — an <c>If-Match</c>/<c>If-None-Match</c> precondition failed.</summary>
    protected IActionResult ApiPreconditionFailed(string message = "The item changed since it was last seen.")
        => StatusCode(StatusCodes.Status412PreconditionFailed, new ApiError("precondition_failed", message));

    /// <summary>422 — the idempotency key was already used for a different request.</summary>
    protected IActionResult ApiIdempotencyConflict(
        string message = "This idempotency key was already used for a different request.")
        => StatusCode(StatusCodes.Status422UnprocessableEntity, new ApiError("idempotency_key_conflict", message));

    // ─────────────────────────── idempotency ───────────────────────────

    /// <summary>The device id carried by the current client-API token, if any.</summary>
    protected Guid? CurrentDeviceId
        => Guid.TryParse(User.FindFirstValue(JwtTokenService.DeviceIdClaim), out var d) ? d : null;

    // Web defaults → camelCase, matching how MVC serializes the live responses, so a
    // replayed body is byte-for-byte what the original request produced.
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs <paramref name="action"/> under at-most-once semantics when the request
    /// carries an <c>Idempotency-Key</c>. A first-seen key executes the action and
    /// stores its outcome; a repeat with the same request replays the stored result
    /// (so a retry after a lost response never re-executes); a repeat with a
    /// different request is rejected as a key conflict. Without the header the action
    /// runs unchanged — idempotency is strictly opt-in.
    /// </summary>
    protected async Task<IActionResult> ExecuteIdempotentAsync(
        Guid userId, string fingerprint, Func<Task<IActionResult>> action)
    {
        var rawKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(rawKey) || CurrentDeviceId is not { } deviceId)
            return await action();

        var key = rawKey.Trim();
        if (key.Length > 128)
            return ApiBadRequest("invalid_idempotency_key", "Idempotency-Key must be at most 128 characters.");

        var receipts = HttpContext.RequestServices.GetRequiredService<IClientRequestReceiptRepository>();

        var existing = await receipts.GetAsync(deviceId, key);
        switch (IdempotencyDecision.Decide(existing?.RequestHash, fingerprint))
        {
            case IdempotencyOutcome.Replay:
                return ReplayReceipt(existing!);
            case IdempotencyOutcome.Conflict:
                return ApiIdempotencyConflict();
        }

        var result = await action();

        if (TryCaptureResult(result, out int status, out string? body) && ShouldStoreOutcome(status))
        {
            var inserted = await receipts.TryInsertAsync(new ClientRequestReceipt
            {
                DeviceId = deviceId,
                UserId = userId,
                IdempotencyKey = key,
                RequestHash = fingerprint,
                StatusCode = status,
                ResponseBody = body,
            });

            // Lost the insert race with a concurrent identical request → replay the winner.
            if (!inserted)
            {
                var winner = await receipts.GetAsync(deviceId, key);
                if (winner is not null && winner.RequestHash == fingerprint)
                    return ReplayReceipt(winner);
            }
        }

        return result;
    }

    private IActionResult ReplayReceipt(ClientRequestReceipt receipt)
        => receipt.ResponseBody is null
            ? StatusCode(receipt.StatusCode)
            : new ContentResult
            {
                StatusCode = receipt.StatusCode,
                Content = receipt.ResponseBody,
                ContentType = "application/json",
            };

    // Only durable, deterministic outcomes are stored: successes and the two
    // condition-based rejections. Auth/not-found/5xx are left unstored so a later
    // retry (with fixed state or credentials) can still succeed.
    private static bool ShouldStoreOutcome(int status)
        => (status is >= 200 and < 300)
           || status == StatusCodes.Status412PreconditionFailed
           || status == StatusCodes.Status409Conflict;

    private static bool TryCaptureResult(IActionResult result, out int status, out string? body)
    {
        switch (result)
        {
            case ObjectResult o:
                status = o.StatusCode ?? StatusCodes.Status200OK;
                body = o.Value is null ? null : JsonSerializer.Serialize(o.Value, ReceiptJson);
                return true;
            case ContentResult c:
                status = c.StatusCode ?? StatusCodes.Status200OK;
                body = c.Content;
                return true;
            case StatusCodeResult s:
                status = s.StatusCode;
                body = null;
                return true;
            default:
                status = 0;
                body = null;
                return false;
        }
    }
}
