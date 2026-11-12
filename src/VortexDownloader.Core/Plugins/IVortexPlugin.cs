namespace VortexDownloader.Core.Plugins;

/// <summary>Identifies an application plugin that is compiled and trusted by the host.</summary>
public interface IVortexPlugin
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
    string Description { get; }
}

/// <summary>A provider that recognizes media URLs and supplies the media workflow.</summary>
public interface IMediaProviderPlugin : IVortexPlugin
{
    IReadOnlyCollection<string> SupportedHosts { get; }
    bool CanHandle(Uri uri);
}
