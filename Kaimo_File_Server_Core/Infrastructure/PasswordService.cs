using Kaimo_File_Server_Core.Core.Security;
using System;
using System.Collections.Generic;
using System.Text;

namespace Kaimo_File_Server_Core.Infrastructure
{
    public class PasswordService : IPasswordService
    {
        // BCrypt für HTTP/Web
        public string HashPassword(string password)
            => BCrypt.Net.BCrypt.HashPassword(password);

        public bool VerifyPassword(string password, string hash)
            => BCrypt.Net.BCrypt.Verify(password, hash);

        // NT-Hash für SMB/NTLM = MD4(UTF-16LE(password))
        public string ComputeNtHash(string password)
        {
            byte[] unicodeBytes = Encoding.Unicode.GetBytes(password);
            byte[] hash = MD4Hash(unicodeBytes);
            return Convert.ToHexString(hash);
        }

        // MD4 Implementierung (nicht in .NET enthalten)
        private static byte[] MD4Hash(byte[] input)
        {
            // MD4 nach RFC 1320
            uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;

            int originalLength = input.Length;
            int paddedLength = ((originalLength + 8) / 64 + 1) * 64;
            byte[] padded = new byte[paddedLength];
            Array.Copy(input, padded, originalLength);
            padded[originalLength] = 0x80;
            BitConverter.GetBytes((ulong)originalLength * 8).CopyTo(padded, paddedLength - 8);

            for (int i = 0; i < paddedLength; i += 64)
            {
                uint[] x = new uint[16];
                for (int j = 0; j < 16; j++)
                    x[j] = BitConverter.ToUInt32(padded, i + j * 4);

                uint aa = a, bb = b, cc = c, dd = d;

                // Round 1
                a = R1(a, b, c, d, x[0], 3); d = R1(d, a, b, c, x[1], 7);
                c = R1(c, d, a, b, x[2], 11); b = R1(b, c, d, a, x[3], 19);
                a = R1(a, b, c, d, x[4], 3); d = R1(d, a, b, c, x[5], 7);
                c = R1(c, d, a, b, x[6], 11); b = R1(b, c, d, a, x[7], 19);
                a = R1(a, b, c, d, x[8], 3); d = R1(d, a, b, c, x[9], 7);
                c = R1(c, d, a, b, x[10], 11); b = R1(b, c, d, a, x[11], 19);
                a = R1(a, b, c, d, x[12], 3); d = R1(d, a, b, c, x[13], 7);
                c = R1(c, d, a, b, x[14], 11); b = R1(b, c, d, a, x[15], 19);

                // Round 2
                a = R2(a, b, c, d, x[0], 3); d = R2(d, a, b, c, x[4], 5);
                c = R2(c, d, a, b, x[8], 9); b = R2(b, c, d, a, x[12], 13);
                a = R2(a, b, c, d, x[1], 3); d = R2(d, a, b, c, x[5], 5);
                c = R2(c, d, a, b, x[9], 9); b = R2(b, c, d, a, x[13], 13);
                a = R2(a, b, c, d, x[2], 3); d = R2(d, a, b, c, x[6], 5);
                c = R2(c, d, a, b, x[10], 9); b = R2(b, c, d, a, x[14], 13);
                a = R2(a, b, c, d, x[3], 3); d = R2(d, a, b, c, x[7], 5);
                c = R2(c, d, a, b, x[11], 9); b = R2(b, c, d, a, x[15], 13);

                // Round 3
                a = R3(a, b, c, d, x[0], 3); d = R3(d, a, b, c, x[8], 9);
                c = R3(c, d, a, b, x[4], 11); b = R3(b, c, d, a, x[12], 15);
                a = R3(a, b, c, d, x[2], 3); d = R3(d, a, b, c, x[10], 9);
                c = R3(c, d, a, b, x[6], 11); b = R3(b, c, d, a, x[14], 15);
                a = R3(a, b, c, d, x[1], 3); d = R3(d, a, b, c, x[9], 9);
                c = R3(c, d, a, b, x[5], 11); b = R3(b, c, d, a, x[13], 15);
                a = R3(a, b, c, d, x[3], 3); d = R3(d, a, b, c, x[11], 9);
                c = R3(c, d, a, b, x[7], 11); b = R3(b, c, d, a, x[15], 15);

                a += aa; b += bb; c += cc; d += dd;
            }

            byte[] result = new byte[16];
            BitConverter.GetBytes(a).CopyTo(result, 0);
            BitConverter.GetBytes(b).CopyTo(result, 4);
            BitConverter.GetBytes(c).CopyTo(result, 8);
            BitConverter.GetBytes(d).CopyTo(result, 12);
            return result;
        }

        private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));
        private static uint R1(uint a, uint b, uint c, uint d, uint x, int s)
            => RotateLeft(a + ((b & c) | (~b & d)) + x, s);
        private static uint R2(uint a, uint b, uint c, uint d, uint x, int s)
            => RotateLeft(a + ((b & c) | (b & d) | (c & d)) + x + 0x5A827999, s);
        private static uint R3(uint a, uint b, uint c, uint d, uint x, int s)
            => RotateLeft(a + (b ^ c ^ d) + x + 0x6ED9EBA1, s);

        public bool VerifyNtHash(string ntHash, string password)
        {
            return ComputeNtHash(password) == ntHash;
        }
    }
}
