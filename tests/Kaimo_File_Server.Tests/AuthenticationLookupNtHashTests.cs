using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Security;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Covers the NT-hash read path of <see cref="AuthenticationLookup.GetNtHashAsync"/>: the at-rest
/// value must be decrypted before use, legacy plaintext rows must keep working, and the disabled /
/// empty-password guards must still hold.
/// </summary>
public class AuthenticationLookupNtHashTests
{
    private readonly PasswordService _passwords = new();
    private readonly AesGcmNtHashProtector _protector =
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { ["NtHash:EncryptionKey"] = "auth-lookup-test-key" })
            .Build());

    private AuthenticationLookup BuildSut(User? user)
    {
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync(user);

        return new AuthenticationLookup(
            userRepo.Object,
            Mock.Of<IShareRepository>(),
            Mock.Of<IAclService>(),
            Mock.Of<IUserContextFactory>(),
            _passwords,
            _protector);
    }

    private User MakeUser(string storedNtHash, bool enabled = true)
        => new(Guid.NewGuid(), "Test", "test", "pw-hash", storedNtHash, isEnabled: enabled);

    [Fact]
    public async Task GetNtHashAsync_EncryptedHash_DecryptsToRawBytes()
    {
        const string hex = "0123456789ABCDEF0123456789ABCDEF";
        var sut = BuildSut(MakeUser(_protector.Protect(hex)));

        var result = await sut.GetNtHashAsync("test");

        Assert.Equal(Convert.FromHexString(hex), result);
    }

    [Fact]
    public async Task GetNtHashAsync_LegacyPlaintextHash_StillWorks()
    {
        const string hex = "0123456789ABCDEF0123456789ABCDEF";
        var sut = BuildSut(MakeUser(hex)); // stored as plaintext (pre-encryption row)

        var result = await sut.GetNtHashAsync("test");

        Assert.Equal(Convert.FromHexString(hex), result);
    }

    [Fact]
    public async Task GetNtHashAsync_DisabledUser_ReturnsNull()
    {
        var sut = BuildSut(MakeUser(_protector.Protect("0123456789ABCDEF0123456789ABCDEF"), enabled: false));

        Assert.Null(await sut.GetNtHashAsync("test"));
    }

    [Fact]
    public async Task GetNtHashAsync_EmptyPasswordHash_ReturnsNull()
    {
        // The well-known empty-password NT hash must be rejected even when stored encrypted.
        var emptyHash = _passwords.ComputeNtHash(string.Empty);
        var sut = BuildSut(MakeUser(_protector.Protect(emptyHash)));

        Assert.Null(await sut.GetNtHashAsync("test"));
    }

    [Fact]
    public async Task GetNtHashAsync_UnknownUser_ReturnsNull()
    {
        var sut = BuildSut(user: null);

        Assert.Null(await sut.GetNtHashAsync("ghost"));
    }
}
