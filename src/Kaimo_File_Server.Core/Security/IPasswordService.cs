using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server.Core.Security
{
    public interface IPasswordService
    {
        string HashPassword(string password);         // BCrypt
        bool VerifyPassword(string password, string hash); // BCrypt verify
        string ComputeNtHash(string password);        // NT hash for NTLM
        bool VerifyNtHash(string ntHash, string password); // verify
    }
}
