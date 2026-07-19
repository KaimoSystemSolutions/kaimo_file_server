using System.Security.Cryptography;
using Kaimo_File_Server.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Tests <see cref="AesGcmNtHashProtector"/>: round-trip correctness, non-determinism, legacy
/// plaintext pass-through, tamper/wrong-key rejection, fail-closed configuration, and that the
/// encrypted form fits the widened NtHash column.
/// </summary>
public class AesGcmNtHashProtectorTests
{
    // A realistic NT hash: MD4 of the empty password (32 hex chars).
    private const string SampleHash = "31D6CFE0D16AE931B73C59D7E0C089C0";

    private static AesGcmNtHashProtector WithKey(string key)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NtHash:EncryptionKey"] = key })
            .Build());

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var sut = WithKey("some-server-secret");

        var stored = sut.Protect(SampleHash);

        Assert.NotEqual(SampleHash, stored);
        Assert.StartsWith("enc:", stored);
        Assert.Equal(SampleHash, sut.Unprotect(stored));
    }

    [Fact]
    public void Protect_IsNonDeterministic_ButBothDecryptToSameValue()
    {
        var sut = WithKey("some-server-secret");

        var a = sut.Protect(SampleHash);
        var b = sut.Protect(SampleHash);

        Assert.NotEqual(a, b); // random nonce per call
        Assert.Equal(SampleHash, sut.Unprotect(a));
        Assert.Equal(SampleHash, sut.Unprotect(b));
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnedUnchanged()
    {
        var sut = WithKey("some-server-secret");

        // A pre-encryption row has no "enc:" prefix → passes through so old data keeps working.
        Assert.Equal(SampleHash, sut.Unprotect(SampleHash));
    }

    [Fact]
    public void Protect_AlreadyEncrypted_IsNotDoubleWrapped()
    {
        var sut = WithKey("some-server-secret");

        var once = sut.Protect(SampleHash);
        var twice = sut.Protect(once);

        Assert.Equal(once, twice);
        Assert.Equal(SampleHash, sut.Unprotect(twice));
    }

    [Fact]
    public void Unprotect_WithWrongKey_Throws()
    {
        var stored = WithKey("key-one").Protect(SampleHash);

        Assert.ThrowsAny<CryptographicException>(() => WithKey("key-two").Unprotect(stored));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var sut = WithKey("some-server-secret");
        var stored = sut.Protect(SampleHash);

        // Flip a character in the base64 body (after the "enc:" prefix).
        var body = stored["enc:".Length..].ToCharArray();
        body[^1] = body[^1] == 'A' ? 'B' : 'A';
        var tampered = "enc:" + new string(body);

        Assert.ThrowsAny<CryptographicException>(() => sut.Unprotect(tampered));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_MissingKey_FailsClosed(string? key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NtHash:EncryptionKey"] = key })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new AesGcmNtHashProtector(config));
    }

    [Fact]
    public void Protect_EmptyString_ReturnsEmpty()
    {
        var sut = WithKey("some-server-secret");
        Assert.Equal("", sut.Protect(""));
    }

    [Fact]
    public void Protect_EncryptedForm_FitsColumn()
    {
        var sut = WithKey("some-server-secret");
        Assert.True(sut.Protect(SampleHash).Length <= 256);
    }
}
