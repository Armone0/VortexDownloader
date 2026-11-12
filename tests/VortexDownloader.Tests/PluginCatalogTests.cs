using VortexDownloader.Core.Plugins;

namespace VortexDownloader.Tests;

public class PluginCatalogTests
{
    private static ManifestMediaProviderPlugin Youtube() => new(PluginStore.YouTube);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://music.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    public void ResolveMediaProvider_FindsYouTubePlugin(string url)
    {
        var plugin = Youtube();
        var catalog = new PluginCatalog([plugin]);
        Assert.Same(plugin, catalog.ResolveMediaProvider(new Uri(url)));
    }

    [Theory]
    [InlineData("https://notyoutube.com/watch?v=abc")]
    [InlineData("https://youtube.com.example.org/watch?v=abc")]
    [InlineData("ftp://youtube.com/file")]
    [InlineData("https://vimeo.com/123")]
    public void ResolveMediaProvider_RejectsUnsupportedHostsAndSchemes(string url)
    {
        var catalog = new PluginCatalog([Youtube()]);
        Assert.Null(catalog.ResolveMediaProvider(new Uri(url)));
    }

    [Fact]
    public void Register_RejectsDuplicatePluginIds()
    {
        var catalog = new PluginCatalog([Youtube()]);
        Assert.Throws<InvalidOperationException>(() => catalog.Register(Youtube()));
    }

    [Fact]
    public void PluginCatalog_ExposesRegisteredPluginMetadata()
    {
        var catalog = new PluginCatalog([Youtube()]);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("media.youtube", plugin.Id);
        Assert.Equal(new Version(1, 0, 0), plugin.Version);
    }
}
