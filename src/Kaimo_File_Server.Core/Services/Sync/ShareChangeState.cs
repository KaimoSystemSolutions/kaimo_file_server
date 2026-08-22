using System;

namespace Kaimo_File_Server.Core.Services.Sync
{
    /// <summary>
    /// A cheap, transport-agnostic fingerprint of a share subtree's persisted
    /// <c>file_metadata</c>, used to answer "did anything change since token X?"
    /// without walking the tree.
    ///
    /// It is derived from a single aggregate query — the newest
    /// <c>ModifiedAt</c> and the item count under the subtree — so a change made
    /// through <em>any</em> transport (web UI, SMB, the client API itself) moves
    /// the token: a create/modify bumps <see cref="MaxModifiedUtc"/>, and a delete
    /// changes <see cref="ItemCount"/>. It is intentionally coarse: it detects
    /// "something changed", after which the client fetches the precise delta.
    /// </summary>
    public readonly record struct ShareChangeState(DateTime? MaxModifiedUtc, long ItemCount)
    {
        /// <summary>Empty state for a subtree with no items.</summary>
        public static readonly ShareChangeState Empty = new(null, 0);

        /// <summary>
        /// Opaque token string handed to clients and passed back on the next
        /// long-poll. Encodes both components so adds/modifies and deletes are
        /// both observable.
        /// </summary>
        public string ToToken() => $"{(MaxModifiedUtc?.Ticks ?? 0L)}:{ItemCount}";

        /// <summary>Parses a token produced by <see cref="ToToken"/>; returns <see cref="Empty"/> on any malformed input.</summary>
        public static ShareChangeState FromToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return Empty;

            var parts = token.Split(':', 2);
            if (parts.Length != 2
                || !long.TryParse(parts[0], out var ticks)
                || !long.TryParse(parts[1], out var count))
                return Empty;

            return new ShareChangeState(
                ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc),
                count);
        }
    }
}
