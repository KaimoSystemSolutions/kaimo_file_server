using System;
using Kaimo_File_Server.Core.Helpers;

namespace Kaimo_File_Server.Core.Domain
{
    /// <summary>
    /// Configuration record for a network share (SMB/HTTP).
    ///
    /// The <see cref="Name"/> serves as both the directory name on disk
    /// and the share name advertised to clients. <see cref="Path"/>
    /// points to the physical storage location on the host file system.
    ///
    /// Each share belongs to exactly ONE department via <see cref="DepartmentId"/>.
    /// Shares without explicit assignment default to the Global department.
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
        /// The department this share belongs to.
        /// Determines which department-scoped administrators can manage this share
        /// and which department default file permissions apply.
        /// Defaults to <see cref="WellKnownDepartments.GlobalId"/> (Global department).
        /// A share always belongs to exactly one department.
        /// </summary>
        public Guid DepartmentId { get; set; } = WellKnownDepartments.GlobalId;

        /// <summary>
        /// When <c>false</c>, the share is hidden from directory listings
        /// and all access attempts are rejected.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// When <c>true</c>, the share is hidden from directory listings but
        /// still accessible to users with direct proper rights.
        /// </summary>
        public bool IsShareHidden { get; set; }

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
        /// <param name="departmentId">
        /// Department this share belongs to.
        /// Pass <c>null</c> or omit to default to the Global department.
        /// </param>
        /// <param name="isEnabled">Whether the share is active on creation.</param>
        /// <param name="isRecycleEnabled">Whether the recycle bin is active.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> or <paramref name="path"/> is blank.
        /// </exception>
        public ShareDefinition(
            string name,
            string path,
            Guid? departmentId = null,
            bool isEnabled = true,
            bool isShareHidden = false,
            bool isRecycleEnabled = false)
        {
            Id = Guid.NewGuid();

            Name = !string.IsNullOrWhiteSpace(name)
                ? name
                : throw new ArgumentException("Name must not be empty.", nameof(name));

            Path = !string.IsNullOrWhiteSpace(path)
                ? path
                : throw new ArgumentException("Path must not be empty.", nameof(path));

            DepartmentId = departmentId ?? WellKnownDepartments.GlobalId;
            IsShareHidden = isShareHidden;
            IsEnabled = isEnabled;
            IsRecycleEnabled = isRecycleEnabled;
        }
    }
}