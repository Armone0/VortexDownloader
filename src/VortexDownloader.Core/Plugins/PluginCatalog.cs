namespace VortexDownloader.Core.Plugins;

/// <summary>Small registry for host-compiled plugins; downloaded assemblies are never loaded.</summary>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, IVortexPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public PluginCatalog(IEnumerable<IVortexPlugin>? plugins = null)
    {
        if (plugins is null) return;
        foreach (var plugin in plugins) Register(plugin);
    }

    public IReadOnlyCollection<IVortexPlugin> Plugins => _plugins.Values.ToArray();

    public void Register(IVortexPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin.Id);
        if (!_plugins.TryAdd(plugin.Id, plugin))
            throw new InvalidOperationException($"A plugin with id '{plugin.Id}' is already registered.");
    }

    public TPlugin? Get<TPlugin>(string id) where TPlugin : class, IVortexPlugin =>
        _plugins.TryGetValue(id, out var plugin) ? plugin as TPlugin : null;

    public IMediaProviderPlugin? ResolveMediaProvider(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _plugins.Values.OfType<IMediaProviderPlugin>().FirstOrDefault(plugin => plugin.CanHandle(uri));
    }
}
