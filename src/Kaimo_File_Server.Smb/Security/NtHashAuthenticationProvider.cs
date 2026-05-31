using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using System.Security.Cryptography;
using System.Text;

namespace Kaimo_File_Server.Smb.Security
{
    public delegate byte[] GetUserNtHash(string userName);

    public class NtHashAuthenticationProvider : NTLMAuthenticationProviderBase
    {
        private readonly GetUserNtHash _getUserNtHash;

        public NtHashAuthenticationProvider(GetUserNtHash getUserNtHash)
        {
            _getUserNtHash = getUserNtHash;
        }

        public override NTStatus GetChallengeMessage(out object context, byte[] negotiateMessageBytes, out byte[] challengeMessageBytes)
        {
            NegotiateMessage negotiateMessage;
            try { negotiateMessage = new NegotiateMessage(negotiateMessageBytes); }
            catch { context = null; challengeMessageBytes = null; return NTStatus.SEC_E_INVALID_TOKEN; }

            byte[] serverChallenge = GenerateServerChallenge();
            context = new AuthContext(serverChallenge);

            ChallengeMessage challengeMessage = new ChallengeMessage();
            challengeMessage.NegotiateFlags = NegotiateFlags.TargetNameNegotiated
                | NegotiateFlags.TargetTypeServer | NegotiateFlags.TargetInfo
                | NegotiateFlags.Version | NegotiateFlags.NTLMSessionSecurity;

            if ((negotiateMessage.NegotiateFlags & NegotiateFlags.UnicodeEncoding) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.UnicodeEncoding;
            else if ((negotiateMessage.NegotiateFlags & NegotiateFlags.OEMEncoding) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.OEMEncoding;

            if ((negotiateMessage.NegotiateFlags & NegotiateFlags.ExtendedSessionSecurity) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.ExtendedSessionSecurity;
            else if ((negotiateMessage.NegotiateFlags & NegotiateFlags.LanManagerSessionKey) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.LanManagerSessionKey;

            if ((negotiateMessage.NegotiateFlags & NegotiateFlags.Sign) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.Sign;
            if ((negotiateMessage.NegotiateFlags & NegotiateFlags.Seal) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.Seal;
            if ((negotiateMessage.NegotiateFlags & NegotiateFlags.KeyExchange) != 0)
                challengeMessage.NegotiateFlags |= NegotiateFlags.KeyExchange;

            string serverName = Environment.MachineName;
            challengeMessage.TargetName = serverName;
            challengeMessage.ServerChallenge = serverChallenge;
            challengeMessage.TargetInfo = AVPairUtils.GetAVPairSequence(serverName, serverName);
            challengeMessage.Version = NTLMVersion.Server2003;

            challengeMessageBytes = challengeMessage.GetBytes();
            return NTStatus.SEC_I_CONTINUE_NEEDED;
        }

        public override NTStatus Authenticate(object context, byte[] authenticateMessageBytes)
        {
            AuthenticateMessage authenticateMessage;
            try { authenticateMessage = new AuthenticateMessage(authenticateMessageBytes); }
            catch { return NTStatus.SEC_E_INVALID_TOKEN; }

            var authContext = context as AuthContext;
            if (authContext == null) return NTStatus.SEC_E_INVALID_TOKEN;

            authContext.DomainName = authenticateMessage.DomainName;
            authContext.UserName = authenticateMessage.UserName;
            authContext.WorkStation = authenticateMessage.WorkStation;
            if (authenticateMessage.Version != null)
                authContext.OSVersion = authenticateMessage.Version.ToString();

            if ((authenticateMessage.NegotiateFlags & NegotiateFlags.Anonymous) != 0)
            {
                authContext.IsGuest = true;
                return NTStatus.STATUS_SUCCESS;
            }

            byte[] ntHash = _getUserNtHash(authenticateMessage.UserName);
            if (ntHash == null)
            {
                Console.WriteLine($"[-] User '{authenticateMessage.UserName}' nicht gefunden");
                return NTStatus.STATUS_LOGON_FAILURE;
            }

            byte[] serverChallenge = authContext.ServerChallenge;
            byte[] sessionBaseKey = null;
            bool success;

            if ((authenticateMessage.NegotiateFlags & NegotiateFlags.ExtendedSessionSecurity) != 0)
            {
                if (AuthenticationMessageUtils.IsNTLMv1ExtendedSessionSecurity(authenticateMessage.LmChallengeResponse))
                {
                    success = AuthenticateV1Extended(ntHash, serverChallenge,
                        authenticateMessage.LmChallengeResponse, authenticateMessage.NtChallengeResponse);
                    if (success) sessionBaseKey = new MD4().GetByteHashFromBytes(ntHash);
                }
                else
                {
                    success = AuthenticateV2(authenticateMessage.DomainName,
                        authenticateMessage.UserName, ntHash, serverChallenge,
                        authenticateMessage.LmChallengeResponse, authenticateMessage.NtChallengeResponse);
                    if (success)
                    {
                        byte[] ntV2Hash = ComputeNtV2Hash(ntHash, authenticateMessage.UserName, authenticateMessage.DomainName);
                        byte[] ntProof = new byte[16];
                        Array.Copy(authenticateMessage.NtChallengeResponse, 0, ntProof, 0, 16);
                        sessionBaseKey = new HMACMD5(ntV2Hash).ComputeHash(ntProof);
                    }
                }
            }
            else
            {
                success = AuthenticateV1(ntHash, serverChallenge,
                    authenticateMessage.LmChallengeResponse, authenticateMessage.NtChallengeResponse);
                if (success) sessionBaseKey = new MD4().GetByteHashFromBytes(ntHash);
            }

            if (success)
            {
                if ((authenticateMessage.NegotiateFlags & NegotiateFlags.KeyExchange) != 0 && sessionBaseKey != null)
                    authContext.SessionKey = RC4.Decrypt(sessionBaseKey, authenticateMessage.EncryptedRandomSessionKey);
                else
                    authContext.SessionKey = sessionBaseKey;

                Console.WriteLine($"[+] User '{authenticateMessage.UserName}' authentifiziert");
                return NTStatus.STATUS_SUCCESS;
            }

            Console.WriteLine($"[-] User '{authenticateMessage.UserName}' falsches Passwort");
            return NTStatus.STATUS_LOGON_FAILURE;
        }

        private static bool AuthenticateV1(byte[] ntHash, byte[] serverChallenge, byte[] lmResponse, byte[] ntResponse)
        {
            byte[] expectedNtResponse = NTLMCryptography.DesLongEncrypt(ntHash, serverChallenge);
            return ByteArraysEqual(expectedNtResponse, ntResponse);
        }

        private static bool AuthenticateV1Extended(byte[] ntHash, byte[] serverChallenge, byte[] lmResponse, byte[] ntResponse)
        {
            byte[] clientChallenge = new byte[8];
            Array.Copy(lmResponse, 0, clientChallenge, 0, 8);
            byte[] challengeHash = MD5.Create().ComputeHash(Concat(serverChallenge, clientChallenge));
            byte[] truncatedHash = new byte[8];
            Array.Copy(challengeHash, 0, truncatedHash, 0, 8);
            byte[] expectedResponse = NTLMCryptography.DesLongEncrypt(ntHash, truncatedHash);
            return ByteArraysEqual(expectedResponse, ntResponse);
        }

        private static bool AuthenticateV2(string domainName, string userName, byte[] ntHash,
            byte[] serverChallenge, byte[] lmResponse, byte[] ntResponse)
        {
            byte[] ntV2Hash = ComputeNtV2Hash(ntHash, userName, domainName);
            if (AuthenticationMessageUtils.IsNTLMv2NTResponse(ntResponse))
            {
                byte[] ntProof = new byte[16];
                Array.Copy(ntResponse, 0, ntProof, 0, 16);
                byte[] clientBlob = new byte[ntResponse.Length - 16];
                Array.Copy(ntResponse, 16, clientBlob, 0, clientBlob.Length);
                byte[] expectedProof = new HMACMD5(ntV2Hash).ComputeHash(Concat(serverChallenge, clientBlob));
                return ByteArraysEqual(ntProof, expectedProof);
            }
            return false;
        }

        private static byte[] ComputeNtV2Hash(byte[] ntHash, string userName, string domainName)
        {
            byte[] identity = Encoding.Unicode.GetBytes(userName.ToUpper() + domainName);
            return new HMACMD5(ntHash).ComputeHash(identity);
        }

        public override bool DeleteSecurityContext(ref object context) { context = null; return true; }

        public override object GetContextAttribute(object context, GSSAttributeName attributeName)
        {
            if (context is AuthContext authContext)
            {
                return attributeName switch
                {
                    GSSAttributeName.DomainName => authContext.DomainName,
                    GSSAttributeName.IsGuest => authContext.IsGuest,
                    GSSAttributeName.MachineName => authContext.WorkStation,
                    GSSAttributeName.OSVersion => authContext.OSVersion,
                    GSSAttributeName.SessionKey => authContext.SessionKey,
                    GSSAttributeName.UserName => authContext.UserName,
                    _ => null
                };
            }
            return null;
        }

        private static byte[] GenerateServerChallenge()
        {
            byte[] challenge = new byte[8];
            RandomNumberGenerator.Fill(challenge);
            return challenge;
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }

        public class AuthContext
        {
            public byte[] ServerChallenge;
            public string DomainName;
            public string UserName;
            public string WorkStation;
            public string OSVersion;
            public byte[] SessionKey;
            public bool IsGuest;
            public AuthContext(byte[] serverChallenge) { ServerChallenge = serverChallenge; }
        }
    }
}
