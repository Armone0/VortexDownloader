using VortexDownloader.Core.Common;
using VortexDownloader.Core.Sources;

namespace VortexDownloader.Tests;

public class UpdateAndClipboardTests
{
    [Theory]
    [InlineData("v1.9.2", "1.9.1+a79b16a0c", true)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("1.9.9", "1.10.0", false)]
    [InlineData("v1.9.1", "1.9.1+abc", false)]
    [InlineData("V2.0", "1.9.9", true)]
    [InlineData("v1.9", "1.9.0", false)]
    [InlineData("latest", "1.9.1", false)]
    [InlineData("v1.9.2-beta", "1.9.1", false)]
    [InlineData("", "1.9.1", false)]
    [InlineData("v1.9.2", "?", false)]
    public void IsNewer(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateVersion.IsNewer(latest, current));

    [Fact]
    public void FindNewer_skips_prereleases_and_picks_highest()
    {
        GitHubRelease R(string tag, bool pre = false) => new(tag, tag, null, pre, "", []);
        var found = UpdateVersion.FindNewer([R("v2.0.0", pre: true), R("v1.9.3"), R("v1.10.0"), R("junk"), R("v1.9.0")], "1.9.1");
        Assert.Equal("v1.10.0", found?.Release.Tag);
        Assert.Null(UpdateVersion.FindNewer([R("v1.9.1"), R("v2.0.0", pre: true)], "1.9.1"));
    }

    [Theory]
    [InlineData("https://example.com/files/setup.exe", true)]
    [InlineData("  http://example.com/a/b/movie.MKV?token=abc  ", true)]
    [InlineData("https://example.com/my%20file.zip", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://example.com/article.html", false)]
    [InlineData("https://example.com/page?f=a.zip", false)]
    [InlineData("https://foo.zip/", false)]
    [InlineData("ftp://example.com/a.zip", false)]
    [InlineData("file:///C:/a.zip", false)]
    [InlineData("javascript:alert(1)//a.zip", false)]
    [InlineData("https://example.com/a.zip\nhttps://example.com/b.zip", false)]
    [InlineData("see https://example.com/a.zip", false)]
    [InlineData("/relative/a.zip", false)]
    [InlineData("", false)]
    public void ClipboardFilter(string text, bool expected) =>
        Assert.Equal(expected, ClipboardLinkFilter.TryGetDownloadUrl(text, out _));

    [Fact]
    public void ClipboardFilter_rejects_overlong_urls()
    {
        string url = "https://example.com/" + new string('a', ClipboardLinkFilter.MaxLength) + ".zip";
        Assert.False(ClipboardLinkFilter.TryGetDownloadUrl(url, out _));
    }
}
