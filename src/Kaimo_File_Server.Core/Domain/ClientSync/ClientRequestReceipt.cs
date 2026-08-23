using System;

namespace Kaimo_File_Server.Core.Domain.ClientSync
{
    /// <summary>
    /// A stored outcome of one mutating client-API request, keyed by the caller's
    /// <see cref="IdempotencyKey"/> (the device-side operation id). It lets a retried
    /// request — one whose HTTP response was lost after the server already applied
    /// the change — replay the original result instead of re-executing and producing
    /// a spurious error (e.g. a delete that now returns 404, or a rename that now
    /// conflicts).
    ///
    /// Idempotency is scoped per device: the same key from a different device is a
    /// different operation. Receipts are disposable — they are pruned after a
    /// retention window and cascade away when the device is revoked.
    /// </summary>
    public sealed class ClientRequestReceipt
    {
        /// <summary>Surrogate primary key.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>The device that issued the request (idempotency scope).</summary>
        public Guid DeviceId { get; set; }

        /// <summary>
        /// The owning user. Denormalized (no FK) for auditing/pruning by user; the
        /// authoritative cascade is through <see cref="DeviceId"/>.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// The client-supplied <c>Idempotency-Key</c> — the device's operation id.
        /// Unique together with <see cref="DeviceId"/>.
        /// </summary>
        public string IdempotencyKey { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 (hex) of the request's identity — method, path, query and a small
        /// body discriminator. A retry that reuses the key with a <em>different</em>
        /// request is rejected rather than replayed, so a key can never mask a
        /// mismatched payload.
        /// </summary>
        public string RequestHash { get; set; } = string.Empty;

        /// <summary>The HTTP status code the original request produced.</summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// The original response body verbatim (JSON), or <c>null</c> when the
        /// original result had no body (e.g. a 204). Replayed as-is.
        /// </summary>
        public string? ResponseBody { get; set; }

        /// <summary>UTC time the receipt was stored (drives retention pruning).</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
