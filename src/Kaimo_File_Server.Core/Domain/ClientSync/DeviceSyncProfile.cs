using System;
using Kaimo_File_Server.Core.Services.DataServices;

namespace Kaimo_File_Server.Core.Domain.ClientSync
{
    /// <summary>
    /// One folder-sync selection for a <see cref="SyncDevice"/>: which share
    /// subtree the device syncs and in which direction.
    ///
    /// The selection is stored server-side (so it roams across a user's devices
    /// and is visible/editable from the web UI) but is authored by the user for
    /// their own device. It reuses the existing <see cref="SyncMode"/> vocabulary:
    ///   • <see cref="SyncMode.Pull"/>  — download-only  (server → device)
    ///   • <see cref="SyncMode.Push"/>  — upload-only    (device → server)
    ///   • <see cref="SyncMode.TwoWay"/> — both directions
    ///
    /// This is deliberately separate from <c>SyncDefinition</c>/<c>SyncedFolder</c>,
    /// which model server-to-cloud-provider (OneDrive/Google) sync. Same
    /// direction vocabulary, different endpoint and lifecycle. The actual sync
    /// loop runs on the client; this record only records intent.
    /// </summary>
    public sealed class DeviceSyncProfile
    {
        /// <summary>Unique identifier for this sync selection.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>The device this selection belongs to.</summary>
        public Guid DeviceId { get; set; }

        /// <summary>
        /// The owning user. Redundant with the device's owner, but stored so a
        /// user's selections can be queried without joining through the device.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>The share whose subtree is synced.</summary>
        public Guid ShareId { get; set; }

        /// <summary>
        /// The <em>remote</em> endpoint: share-relative root of the synced subtree
        /// (forward slashes, no leading or trailing slash; empty string = whole
        /// share). Always validated and normalized through <c>ShareRelativePath</c>
        /// before persistence.
        /// </summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>
        /// The <em>local</em> endpoint: the folder path on the device that is
        /// paired with <see cref="RelativePath"/>. It is chosen and owned entirely
        /// by the client and is opaque to the server — the server never resolves,
        /// validates, or touches it. It is stored so the device's own sync
        /// configuration travels with the connection and can be shown for
        /// reference. May be empty for clients that track the local side themselves.
        /// </summary>
        public string LocalPath { get; set; } = string.Empty;

        /// <summary>Sync direction. See the type remarks for the mapping.</summary>
        public SyncMode Mode { get; set; } = SyncMode.TwoWay;

        /// <summary>
        /// When <c>false</c>, the client should keep the selection but stop syncing
        /// it, without the user having to delete and re-create it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>UTC timestamp the selection was created.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>UTC timestamp of the last change to the selection.</summary>
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
