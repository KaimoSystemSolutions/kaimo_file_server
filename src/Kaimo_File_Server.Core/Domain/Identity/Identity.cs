using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    /// <summary>
    /// Abstract base class for all security principals (users, groups, roles).
    /// Provides a unique <see cref="Id"/> and a human-readable <see cref="Name"/>.
    /// </summary>
    public abstract class Identity
    {
        /// <summary>Globally unique identifier for this principal.</summary>
        public Guid Id { get; init; }

        /// <summary>
        /// Human-readable display name.
        /// Must not be null or whitespace at construction time.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>EF Core / serialization constructor.</summary>
        protected Identity() { }

        /// <param name="id">Must not be <see cref="Guid.Empty"/>.</param>
        /// <param name="name">Must not be null or whitespace.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="id"/> is empty or <paramref name="name"/> is blank.
        /// </exception>
        protected Identity(Guid id, string name)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("Id must not be empty.", nameof(id));

            Id = id;
            Name = !string.IsNullOrWhiteSpace(name)
                ? name
                : throw new ArgumentException("Name must not be null or whitespace.", nameof(name));
        }
    }
}