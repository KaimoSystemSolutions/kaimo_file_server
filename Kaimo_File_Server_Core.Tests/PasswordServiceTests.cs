using Kaimo_File_Server_Core.Infrastructure;
using Xunit;

namespace Kaimo_File_Server_Core.Tests;

public class PasswordServiceTests
{
    private readonly PasswordService _sut = new();

    // ========== BCrypt ==========

    [Fact]
    public void HashPassword_ReturnsNonEmptyHash()
    {
        var hash = _sut.HashPassword("test1234");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.StartsWith("$2", hash); // BCrypt-Prefix
    }

    [Fact]
    public void HashPassword_DifferentCallsProduceDifferentHashes()
    {
        var hash1 = _sut.HashPassword("test1234");
        var hash2 = _sut.HashPassword("test1234");

        // BCrypt salzt automatisch → verschiedene Hashes
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue()
    {
        var hash = _sut.HashPassword("mypassword");
        Assert.True(_sut.VerifyPassword("mypassword", hash));
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var hash = _sut.HashPassword("mypassword");
        Assert.False(_sut.VerifyPassword("wrongpassword", hash));
    }

    [Fact]
    public void VerifyPassword_EmptyPassword_WorksCorrectly()
    {
        var hash = _sut.HashPassword("");
        Assert.True(_sut.VerifyPassword("", hash));
        Assert.False(_sut.VerifyPassword("notempty", hash));
    }

    // ========== NT-Hash ==========

    [Fact]
    public void ComputeNtHash_ReturnsHexString()
    {
        var hash = _sut.ComputeNtHash("password");

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        // NT-Hash ist MD4 = 16 Bytes = 32 Hex-Zeichen
        Assert.Equal(32, hash.Length);
        Assert.True(hash.All(c => "0123456789ABCDEF".Contains(c)));
    }

    [Fact]
    public void ComputeNtHash_SameInput_SameOutput()
    {
        var hash1 = _sut.ComputeNtHash("test");
        var hash2 = _sut.ComputeNtHash("test");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeNtHash_DifferentInput_DifferentOutput()
    {
        var hash1 = _sut.ComputeNtHash("password1");
        var hash2 = _sut.ComputeNtHash("password2");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeNtHash_KnownValue_Password()
    {
        // Bekannter NT-Hash für "password" (RFC / NTLM-Standard)
        var hash = _sut.ComputeNtHash("password");
        Assert.Equal("8846F7EAEE8FB117AD06BDD830B7586C", hash);
    }

    [Fact]
    public void ComputeNtHash_KnownValue_Password_2()
    {
        // Bekannter NT-Hash für "password" (RFC / NTLM-Standard)
        var hash = _sut.ComputeNtHash("Ab238#-.?sy<ks32hasdbASk234");
        Assert.Equal("E5A715F9C1212D80DFA44D0FE119EFE7", hash);
    }

    [Fact]
    public void ComputeNtHash_EmptyString_ReturnsKnownHash()
    {
        // Bekannter NT-Hash für "" (leeres Passwort)
        var hash = _sut.ComputeNtHash("");
        Assert.Equal("31D6CFE0D16AE931B73C59D7E0C089C0", hash);
    }

    [Fact]
    public void VerifyNtHash_CorrectPassword_ReturnsTrue()
    {
        var hash = _sut.ComputeNtHash("admin1234");
        Assert.True(_sut.VerifyNtHash(hash, "admin1234"));
    }

    [Fact]
    public void VerifyNtHash_WrongPassword_ReturnsFalse()
    {
        var hash = _sut.ComputeNtHash("admin1234");
        Assert.False(_sut.VerifyNtHash(hash, "wrong"));
    }

    // ========== Unicode / Sonderzeichen ==========

    [Fact]
    public void ComputeNtHash_UnicodeCharacters_Works()
    {
        var hash = _sut.ComputeNtHash("Pässwörd!");
        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeNtHash_LongPassword_Works()
    {
        var longPassword = new string('A', 1000);
        var hash = _sut.ComputeNtHash(longPassword);
        Assert.NotNull(hash);
        Assert.Equal(32, hash.Length);
    }
}
