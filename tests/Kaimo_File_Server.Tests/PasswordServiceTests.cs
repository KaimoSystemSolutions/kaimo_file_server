using Kaimo_File_Server.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class PasswordServiceTests
{
    private readonly PasswordService _sut = new();

    [Fact] public void HashPassword_ReturnsNonEmptyHash() { var h = _sut.HashPassword("test1234"); Assert.NotNull(h); Assert.NotEmpty(h); Assert.StartsWith("$2", h); }
    [Fact] public void HashPassword_DifferentCallsProduceDifferentHashes() { Assert.NotEqual(_sut.HashPassword("test1234"), _sut.HashPassword("test1234")); }
    [Fact] public void VerifyPassword_CorrectPassword_ReturnsTrue() { Assert.True(_sut.VerifyPassword("mypassword", _sut.HashPassword("mypassword"))); }
    [Fact] public void VerifyPassword_WrongPassword_ReturnsFalse() { Assert.False(_sut.VerifyPassword("wrongpassword", _sut.HashPassword("mypassword"))); }
    [Fact] public void VerifyPassword_EmptyPassword_WorksCorrectly() { var h = _sut.HashPassword(""); Assert.True(_sut.VerifyPassword("", h)); Assert.False(_sut.VerifyPassword("notempty", h)); }

    [Fact] public void ComputeNtHash_ReturnsHexString() { var h = _sut.ComputeNtHash("password"); Assert.Equal(32, h.Length); Assert.True(h.All(c => "0123456789ABCDEF".Contains(c))); }
    [Fact] public void ComputeNtHash_SameInput_SameOutput() { Assert.Equal(_sut.ComputeNtHash("test"), _sut.ComputeNtHash("test")); }
    [Fact] public void ComputeNtHash_DifferentInput_DifferentOutput() { Assert.NotEqual(_sut.ComputeNtHash("password1"), _sut.ComputeNtHash("password2")); }
    [Fact] public void ComputeNtHash_KnownValue_Password() { Assert.Equal("8846F7EAEE8FB117AD06BDD830B7586C", _sut.ComputeNtHash("password")); }
    [Fact] public void ComputeNtHash_KnownValue_Password_2() { Assert.Equal("E5A715F9C1212D80DFA44D0FE119EFE7", _sut.ComputeNtHash("Ab238#-.?sy<ks32hasdbASk234")); }
    [Fact] public void ComputeNtHash_EmptyString_ReturnsKnownHash() { Assert.Equal("31D6CFE0D16AE931B73C59D7E0C089C0", _sut.ComputeNtHash("")); }
    [Fact] public void VerifyNtHash_CorrectPassword_ReturnsTrue() { Assert.True(_sut.VerifyNtHash(_sut.ComputeNtHash("admin1234"), "admin1234")); }
    [Fact] public void VerifyNtHash_WrongPassword_ReturnsFalse() { Assert.False(_sut.VerifyNtHash(_sut.ComputeNtHash("admin1234"), "wrong")); }
    [Fact] public void ComputeNtHash_LongPassword_Works() { var h = _sut.ComputeNtHash(new string('A', 1000)); Assert.Equal(32, h.Length); }
}
