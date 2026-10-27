using Kaimo_File_Server.Core.Services.File;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class VersioningPolicyStoreTests
{
    private readonly Mock<IConfigRepository> _config = new();
    private readonly VersioningPolicyStore _sut;

    public VersioningPolicyStoreTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_config.Object);
        _sut = new VersioningPolicyStore(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<VersioningPolicyStore>.Instance);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheStoredPolicyNormalized()
    {
        _config.Setup(c => c.GetFreshAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()))
            .ReturnsAsync(new VersioningPolicy { MaxAgeDays = 20, PoolBudgetPercent = 500 });

        var policy = await _sut.GetAsync();

        Assert.Equal(20, policy.MaxAgeDays);
        Assert.Equal(VersioningPolicy.MaxBudgetPercent, policy.PoolBudgetPercent);
    }

    [Fact]
    public async Task GetAsync_CachesAndReturnsIndependentCopies()
    {
        _config.Setup(c => c.GetFreshAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()))
            .ReturnsAsync(VersioningPolicy.Default());

        var first = await _sut.GetAsync();
        first.Enabled = false;
        first.ExcludePatterns.Clear();
        var second = await _sut.GetAsync();

        Assert.True(second.Enabled);
        Assert.NotEmpty(second.ExcludePatterns);
        _config.Verify(c => c.GetFreshAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ConfigUnavailable_FallsBackToDefaults()
    {
        _config.Setup(c => c.GetFreshAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var policy = await _sut.GetAsync();

        Assert.Equal(VersioningPolicy.Default().MaxFileSizeBytes, policy.MaxFileSizeBytes);
    }

    [Fact]
    public async Task SetAsync_PersistsNormalizedPolicyAndServesIt()
    {
        VersioningPolicy? stored = null;
        _config.Setup(c => c.SetAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()))
            .Callback<string, VersioningPolicy>((_, p) => stored = p)
            .Returns(Task.CompletedTask);

        await _sut.SetAsync(new VersioningPolicy { MaxVersionsPerFile = -4, ExcludePatterns = [" *.bak ", ""] });
        var served = await _sut.GetAsync();

        Assert.NotNull(stored);
        Assert.Equal(0, stored.MaxVersionsPerFile);
        Assert.Equal(["*.bak"], stored.ExcludePatterns);
        Assert.Equal(["*.bak"], served.ExcludePatterns);
        _config.Verify(c => c.GetFreshAsync(VersioningPolicy.ConfigKey, It.IsAny<VersioningPolicy>()), Times.Never);
    }

    [Fact]
    public async Task SetAsync_DoesNotKeepAReferenceToTheCallersInstance()
    {
        var policy = VersioningPolicy.Default();
        await _sut.SetAsync(policy);
        policy.Enabled = false;

        Assert.True((await _sut.GetAsync()).Enabled);
    }
}
