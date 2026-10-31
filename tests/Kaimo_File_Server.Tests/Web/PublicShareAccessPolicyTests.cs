using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The single gate every anonymous access to a public download link passes: the download
/// endpoint and the in-page preview consume one access, listing and navigating validate without
/// consuming. Each check reads the link, its share and its creator fresh from the store.
/// </summary>
public sealed class PublicShareAccessPolicyTests
{
    private readonly PublicShareFixture _f = new();
    private readonly PublicShareAccessPolicy _sut;

    public PublicShareAccessPolicyTests() => _sut = _f.CreatePolicy();

    [Fact]
    public async Task Consume_ActiveLink_AllowsAndCountsOneAccess()
    {
        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, "files.example.com", ["projects/secret/a.txt"]);

        Assert.True(access.Allowed);
        Assert.Equal(_f.Share.Id, access.Share!.Id);
        Assert.Equal(_f.Creator.Id, access.Actor!.User.Id);
        Assert.Equal(["projects/secret/a.txt"], access.Paths);
        Assert.Equal(1, _f.Link.AccessCount);
    }

    [Fact]
    public async Task Consume_LastAccess_IsStillAllowed()
    {
        _f.Link.MaxAccessCount = 1;

        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/a.txt"]);

        Assert.True(access.Allowed);
        Assert.Equal(1, _f.Link.AccessCount);
    }

    [Fact]
    public async Task Consume_ExhaustedLink_IsDenied()
    {
        _f.Revoke("exhausted");

        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/a.txt"]);

        Assert.False(access.Allowed);
        Assert.True(access.LinkUnavailable);
        Assert.Equal(PublicShareDenial.LinkUnavailable, access.Denial);
    }

    [Fact]
    public async Task Validate_DoesNotConsume()
    {
        var access = await _sut.ValidateAsync(PublicShareFixture.Token, null);

        Assert.True(access.Allowed);
        Assert.Equal(_f.Creator.Id, access.Actor!.User.Id);
        Assert.Equal(0, _f.Link.AccessCount);
    }

    [Theory]
    [InlineData("disabled", PublicShareDenial.LinkUnavailable)]
    [InlineData("expired", PublicShareDenial.LinkUnavailable)]
    [InlineData("deleted", PublicShareDenial.LinkUnavailable)]
    [InlineData("exhausted", PublicShareDenial.LinkUnavailable)]
    [InlineData("downloadsOff", PublicShareDenial.DownloadLinksDisabled)]
    [InlineData("creatorDisabled", PublicShareDenial.CreatorUnavailable)]
    [InlineData("shareDisabled", PublicShareDenial.ShareUnavailable)]
    public async Task Validate_RevokedLink_IsUnavailable(string how, PublicShareDenial expected)
    {
        _f.Revoke(how);

        var access = await _sut.ValidateAsync(PublicShareFixture.Token, null);

        Assert.False(access.Allowed);
        Assert.True(access.LinkUnavailable);
        Assert.Equal(expected, access.Denial);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("downloadsOff")]
    [InlineData("creatorDisabled")]
    [InlineData("shareDisabled")]
    public async Task Consume_RevokedLink_IsUnavailable(string how)
    {
        _f.Revoke(how);

        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/a.txt"]);

        Assert.False(access.Allowed);
        Assert.True(access.LinkUnavailable);
    }

    [Theory]
    [InlineData("projects/other/a.txt")]
    [InlineData("projects/secretx/a.txt")]
    [InlineData("a.txt")]
    public async Task Consume_PathOutsideLinkRoot_IsRejected(string path)
    {
        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, null, [path]);

        Assert.Equal(PublicShareDenial.OutsideRoot, access.Denial);
        Assert.False(access.LinkUnavailable);
    }

    [Fact]
    public async Task Consume_InvalidPath_IsRejected()
    {
        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/../../etc/passwd"]);

        Assert.Equal(PublicShareDenial.InvalidPath, access.Denial);
        Assert.False(access.LinkUnavailable);
    }

    [Fact]
    public async Task Consume_HostOtherThanDefaultDomain_IsRejectedBeforeTheLinkIsTouched()
    {
        _f.Settings.RestrictToDefaultDomain = true;
        _f.Domains = new ServerDomainSettings { Addresses = ["https://files.example.com"] };

        var access = await _sut.ConsumeAsync(PublicShareFixture.Token, "evil.example.net", ["projects/secret/a.txt"]);

        Assert.Equal(PublicShareDenial.HostNotAllowed, access.Denial);
        Assert.Equal(0, _f.Link.AccessCount);
    }

    [Fact]
    public async Task Consume_ReadOnlyDemo_ChecksPolicyWithoutConsuming()
    {
        _f.DemoReadOnly = true;

        var allowed = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/a.txt"]);
        _f.Revoke("exhausted");
        var denied = await _sut.ConsumeAsync(PublicShareFixture.Token, null, ["projects/secret/a.txt"]);

        Assert.True(allowed.Allowed);
        Assert.Equal(PublicShareDenial.LinkUnavailable, denied.Denial);
    }

    // ── "Link accessed" notification: once per visit ──

    [Fact]
    public async Task Notify_OncePerVisit_AndAgainForANewVisit()
    {
        var visit = Guid.NewGuid();

        await _sut.NotifyAccessedAsync(_f.Link, visit, "a.txt");
        await _sut.NotifyAccessedAsync(_f.Link, visit, "b.txt");
        Assert.Equal(1, _f.Notified);

        await _sut.NotifyAccessedAsync(_f.Link, Guid.NewGuid(), "a.txt");
        Assert.Equal(2, _f.Notified);
    }

    [Fact]
    public async Task Notify_WithoutVisit_AlwaysPublishes()
    {
        await _sut.NotifyAccessedAsync(_f.Link, null, "a.txt");
        await _sut.NotifyAccessedAsync(_f.Link, null, "a.txt");

        Assert.Equal(2, _f.Notified);
    }

    [Fact]
    public void Gate_IsScopedToTheLink()
    {
        var visit = Guid.NewGuid();

        Assert.True(_f.Gate.TryEnter(Guid.NewGuid(), visit));
        Assert.True(_f.Gate.TryEnter(Guid.NewGuid(), visit));
    }
}
