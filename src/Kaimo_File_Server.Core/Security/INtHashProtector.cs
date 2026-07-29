namespace Kaimo_File_Server.Core.Security;

/// <summary>
/// Encrypts the SMB/NTLM NT hash for storage at rest and decrypts it on read.
///
/// The NT hash is password-equivalent (pass-the-hash) and, being unsalted MD4, is trivial to
/// attack offline once the database is exposed. Salting/slow-hashing is impossible because NTLM
/// needs the raw hash, so the remaining lever is encrypting the column with a server-held key.
///
/// Implementations MUST:
///   • be able to <see cref="Unprotect"/> a legacy plaintext value unchanged, so existing rows keep
///     working and get migrated to ciphertext the next time they are written;
///   • be idempotent under <see cref="Protect"/> (protecting an already-protected value is a no-op),
///     so a value that slips through pre-encrypted is never double-wrapped.
/// </summary>
public interface INtHashProtector
{
    /// <summary>Returns the stored (encrypted) representation of a plaintext NT-hash hex string.</summary>
    string Protect(string plaintextNtHashHex);

    /// <summary>Returns the plaintext NT-hash hex string for a stored value (encrypted or legacy plaintext).</summary>
    string Unprotect(string storedNtHash);

    /// <summary>
    /// Returns the decoded raw NT-hash bytes without materializing an
    /// additional managed plaintext string. Implementations must reject
    /// malformed hex and clear temporary plaintext buffers before returning.
    /// </summary>
    byte[] UnprotectToBytes(string storedNtHash);
}
