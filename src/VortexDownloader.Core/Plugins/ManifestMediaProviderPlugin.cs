namespace VortexDownloader.Core.Plugins;

/// <summary>Media provider built from a validated <see cref="PluginManifest"/>.</summary>
public sealed class ManifestMediaProviderPlugin(PluginManifest manifest) : IMediaProviderPlugin
{
    public string Id => manifest.Id;
    public string Name => manifest.Name;
    public Version Version { get; } = Version.Parse(manifest.Version);
    public string Description => manifest.Description;
    public IReadOnlyCollection<string> SupportedHosts => manifest.Hosts;

    public bool CanHandle(Uri uri)
    {
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        var host = uri.IdnHost.TrimEnd('.');
        return manifest.Hosts.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                                       host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }
}
