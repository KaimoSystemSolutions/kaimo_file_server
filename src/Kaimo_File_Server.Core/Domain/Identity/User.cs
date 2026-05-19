using System;

namespace Kaimo_File_Server.Core.Domain.Identity
{
    public class User : Identity
    {
        public string Username { get; init; }
        public string Description { get; set; }
        public string Email { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool CanChangePassword { get; set; } = true;

        // General Hash
        public string PasswordHash { get; init; }
        // Just for SMB NTLM Authentication
        public string NtHash { get; init; }

        protected User() { }

        public User(
            Guid id,
            string name,
            string username,
            string passwordHash,
            string ntHash,
            string description = null,
            string email = null,
            bool isEnabled = true,
            bool canChangePassword = true
        ) : base(id, name)
        {
            Username = username;
            PasswordHash = passwordHash;
            NtHash = ntHash;
            Description = description;
            Email = email;
            IsEnabled = isEnabled;
            CanChangePassword = canChangePassword;
        }
    }
}