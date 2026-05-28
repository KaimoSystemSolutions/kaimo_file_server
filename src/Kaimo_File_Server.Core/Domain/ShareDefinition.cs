using System;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Configuration record for a network share (SMB/HTTP).
    ///
    /// The <see cref="Name"/> serves as both the directory name on disk
    /// and the share name advertised to clients. <see cref="Path"/>
    /// points to the physical storage location on the host file system.
    ///
    /// Shares can be temporarily disabled (<see cref="IsEnabled"/> = false)
    /// without deleting their configuration, and optionally maintain a
    /// per-share recycle bin (<see cref="IsRecycleEnabled"/>).
    /// </summary>
    public class ShareDefinition
    {
        /// <summary>Unique identifier for this share.</summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Share name visible to clients and used as the directory name.
        /// Must be unique across all shares.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path to the share's root directory on the host
        /// file system (e.g. "/data/storage/projekte").
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// When <c>false</c>, the share is hidden from directory listings
        /// and all access attempts are rejected.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// When <c>true</c>, deleted files are moved to a hidden
        /// <c>.recycle</c> directory inside the share instead of
        /// being permanently removed.
        /// </summary>
        public bool IsRecycleEnabled { get; set; }

        /// <summary>EF Core / serialization constructor.</summary>
        internal ShareDefinition() { }

        /// <param name="name">Share / directory name — must not be blank.</param>
        /// <param name="path">Absolute host path — must not be blank.</param>
        /// <param name="isEnabled">Whether the share is active on creation.</param>
        /// <param name="isRecycleEnabled">Whether the recycle bin is active.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> or <paramref name="path"/> is blank.
        /// </exception>
        public ShareDefinition(string name, string path, bool isEnabled = true, bool isRecycleEnabled = false)
        {
            Id = Guid.NewGuid();

            Name = !string.IsNullOrWhiteSpace(name)
                ? name
                : throw new ArgumentException("Name must not be empty.", nameof(name));

            Path = !string.IsNullOrWhiteSpace(path)
                ? path
                : throw new ArgumentException("Path must not be empty.", nameof(path));

            IsEnabled = isEnabled;
            IsRecycleEnabled = isRecycleEnabled;
        }
    }
}