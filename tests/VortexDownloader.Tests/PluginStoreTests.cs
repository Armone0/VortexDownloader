using VortexDownloader.Core.Plugins;

namespace VortexDownloader.Tests;

public class PluginStoreTests
{
    private const string Good = """{"id":"media.vimeo","name":"Vimeo","version":"1.0.0","description":"d","type":"media-provider","hosts":["vimeo.com"]}""";

    [Fact]
    public void Parse_AcceptsValidManifest() => Assert.Equal("media.vimeo", PluginManifest.Parse(Good).Id);

    [Theory]
    [InlineData("""{"id":"../evil","name":"x","version":"1.0.0","description":"d","type":"media-provider","hosts":["a.com"]}""")]
    [InlineData("""{"id":"media.x","name":"x","version":"1.0.0","description":"d","type":"native-dll","hosts":["a.com"]}""")]
    [InlineData("""{"id":"media.x","name":"x","version":"1.0.0","description":"d","type":"media-provider","hosts":["http://a.com/path"]}""")]
    [InlineData("""{"id":"media.x","name":"x","version":"abc","description":"d","type":"media-provider","hosts":["a.com"]}""")]
    [InlineData("not json")]
    public void Parse_RejectsBadManifest(string json) => Assert.Throws<FormatException>(() => PluginManifest.Parse(json));

    [Fact]
    public void Store_SeedsYouTubeOnce_InstallsAndRemoves()
    {
        string root = Path.Combine(Path.GetTempPath(), "vortex-plugins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PluginStore(root);
            store.SeedDefaults();
            Assert.Contains(store.LoadAll(), p => p.Id == "media.youtube");

            store.Remove("media.youtube");
            store.SeedDefaults(); // marker present: must not come back
            Assert.Empty(store.LoadAll());

            store.Install(PluginManifest.Parse(Good));
            var plugin = Assert.IsAssignableFrom<IMediaProviderPlugin>(Assert.Single(store.LoadAll()));
            Assert.True(plugin.CanHandle(new Uri("https://player.vimeo.com/x")));
            Assert.False(plugin.CanHandle(new Uri("https://notvimeo.com/x")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
