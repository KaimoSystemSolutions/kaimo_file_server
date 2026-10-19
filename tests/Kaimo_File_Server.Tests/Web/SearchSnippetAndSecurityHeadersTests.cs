using Kaimo_File_Server.Web.Components.Search;
using Kaimo_File_Server.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Stored XSS via the global search: highlight snippets carry user-controlled file
/// names/content and used to be rendered as raw HTML. They are now split into text
/// segments (encoded by Blazor), and every response carries a restrictive CSP.
/// </summary>
public class SearchSnippetAndSecurityHeadersTests
{
    [Fact]
    public void Split_KeepsInjectedMarkupAsPlainText()
    {
        var segments = SearchSnippet.Split("a <img src=x onerror=alert(1)> <mark>report</mark> b").ToList();

        Assert.Equal(
            [
                new SearchSnippet.Segment("a <img src=x onerror=alert(1)> ", false),
                new SearchSnippet.Segment("report", true),
                new SearchSnippet.Segment(" b", false),
            ],
            segments);
    }

    [Fact]
    public void Split_MarkupInsideHighlightStaysText()
    {
        var segments = SearchSnippet.Split("<mark><script>x</script></mark>").ToList();

        Assert.Equal([new SearchSnippet.Segment("<script>x</script>", true)], segments);
    }

    [Theory]
    [InlineData("plain name.txt")]
    [InlineData("unclosed <mark>tail")]
    [InlineData("</mark> stray close")]
    public void Split_WithoutCompleteMarkerPair_ReturnsWholeTextUnhighlighted(string snippet)
    {
        Assert.Equal([new SearchSnippet.Segment(snippet, false)], SearchSnippet.Split(snippet).ToList());
    }

    [Fact]
    public void Split_Empty_ReturnsNothing()
    {
        Assert.Empty(SearchSnippet.Split(null));
        Assert.Empty(SearchSnippet.Split(""));
    }

    [Fact]
    public async Task Middleware_SetsSecurityHeaders_WithPerRequestNonce()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var headers = context.Response.Headers;
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", headers["Referrer-Policy"].ToString());
        Assert.Equal("SAMEORIGIN", headers.XFrameOptions.ToString());

        string csp = headers.ContentSecurityPolicy.ToString();
        string nonce = SecurityHeadersMiddleware.GetNonce(context);
        Assert.Contains($"script-src 'self' 'nonce-{nonce}';", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    [Fact]
    public void Nonce_IsStablePerRequest_AndDiffersBetweenRequests()
    {
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();

        Assert.Equal(SecurityHeadersMiddleware.GetNonce(first), SecurityHeadersMiddleware.GetNonce(first));
        Assert.NotEqual(SecurityHeadersMiddleware.GetNonce(first), SecurityHeadersMiddleware.GetNonce(second));
    }
}
