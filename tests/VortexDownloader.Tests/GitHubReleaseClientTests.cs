using System.Net;
using System.Text;
using VortexDownloader.Core.Sources;

namespace VortexDownloader.Tests;

public class GitHubReleaseClientTests
{
    private sealed class FakeHandler(HttpStatusCode code, string body, params (string, string)[] headers) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in headers) r.Headers.Add(k, v);
            return Task.FromResult(r);
        }
    }

    [Theory]
    [InlineData("https://github.com/o/r", "o", "r", null)]
    [InlineData("https://github.com/o/r/", "o", "r", null)]
    [InlineData("https://github.com/o/r.git", "o", "r", null)]
    [InlineData("https://github.com/o/r/releases", "o", "r", null)]
    [InlineData("https://github.com/o/r/releases/", "o", "r", null)]
    [InlineData("https://github.com/o/r/releases/latest", "o", "r", null)]
    [InlineData("https://github.com/o/r/releases/tag/v1.2.3", "o", "r", "v1.2.3")]
    [InlineData("https://www.github.com/o/my.repo/releases/tag/v1/rc", "o", "my.repo", "v1/rc")]
    public void TryParseRepo_accepts(string url, string o, string r, string? t)
    {
        Assert.True(GitHubReleaseClient.TryParseRepo(url, out var owner, out var repo, out var tag));
        Assert.Equal((o, r, t), (owner, repo, tag));
    }

    [Theory]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://evilgithub.com/o/r")]
    [InlineData("https://github.com.evil.com/o/r")]
    [InlineData("http://github.com/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/o/r/issues")]
    [InlineData("https://github.com/o/r/releases/download/v1/a.zip")]
    [InlineData("https://github.com/../r")]
    [InlineData("not a url")]
    [InlineData("")]
    public void TryParseRepo_rejects(string url) =>
        Assert.False(GitHubReleaseClient.TryParseRepo(url, out _, out _, out _));

    private const string Json = """
    [
     {"name":"One","tag_name":"v1","published_at":"2026-01-02T03:04:05Z","prerelease":false,"draft":false,"body":"notes",
      "assets":[
        {"name":"a.zip","size":100,"download_count":7,"browser_download_url":"https://github.com/o/r/releases/download/v1/a.zip","digest":"sha256:ABCDEF"},
        {"name":"b.zip","size":5,"download_count":1,"browser_download_url":"https://github.com/o/r/releases/download/v1/b.zip","digest":null},
        {"name":"evil","size":5,"download_count":1,"browser_download_url":"https://evil.com/x"},
        {"name":"http","size":5,"download_count":1,"browser_download_url":"http://github.com/x"}]},
     {"name":"","tag_name":"v2-beta","published_at":"2026-02-01T00:00:00Z","prerelease":true,"draft":false,"body":null,"assets":[]},
     {"name":"Hidden","tag_name":"v3","prerelease":false,"draft":true,"assets":[]}
    ]
    """;

    [Fact]
    public async Task Parses_releases_skips_drafts_and_bad_urls_and_reads_digest()
    {
        var h = new FakeHandler(HttpStatusCode.OK, Json);
        var list = await new GitHubReleaseClient(new HttpClient(h)).GetReleasesAsync("o", "r");

        Assert.Equal(2, list.Count);
        Assert.Equal("One", list[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), list[0].PublishedAt);
        Assert.Equal(2, list[0].Assets.Count);
        Assert.Equal("abcdef", list[0].Assets[0].Sha256);
        Assert.Equal(7, list[0].Assets[0].DownloadCount);
        Assert.Null(list[0].Assets[1].Sha256);
        Assert.Equal("v2-beta", list[1].Name);
        Assert.True(list[1].Prerelease);
        Assert.Equal("", list[1].Body);
        Assert.Equal("https://api.github.com/repos/o/r/releases?per_page=30", h.Last!.RequestUri!.ToString());
        Assert.StartsWith("VortexDownloader/", h.Last.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", h.Last.Headers.Accept.ToString());
    }

    [Fact]
    public async Task RateLimit_throws_with_reset_time()
    {
        var h = new FakeHandler(HttpStatusCode.Forbidden, "{}", ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "1800000000"));
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => new GitHubReleaseClient(new HttpClient(h)).GetReleasesAsync("o", "r"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), ex.ResetAt);
    }

    [Fact]
    public async Task NotFound_throws_typed()
    {
        var h = new FakeHandler(HttpStatusCode.NotFound, "{}");
        await Assert.ThrowsAsync<GitHubRepoNotFoundException>(() => new GitHubReleaseClient(new HttpClient(h)).GetReleasesAsync("o", "r"));
    }

    [Fact]
    public async Task Oversized_response_rejected()
    {
        var h = new FakeHandler(HttpStatusCode.OK, new string(' ', GitHubReleaseClient.MaxResponseBytes + 10));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseClient(new HttpClient(h)).GetReleasesAsync("o", "r"));
    }
}
