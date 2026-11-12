using System.Windows.Input;
using VortexDownloader.Core.Plugins;

namespace VortexDownloader.UI.ViewModels;

/// <summary>Display model for one plugin: card on the Plugins page and (when favorited) quick entry in the sidebar.</summary>
public sealed class PluginCardViewModel : ViewModelBase
{
    private bool _isFavorite;

    public PluginCardViewModel(IVortexPlugin plugin, bool isFavorite, Action open, Action<PluginCardViewModel> toggleFavorite, Action<PluginCardViewModel> remove)
    {
        Id = plugin.Id;
        Name = plugin.Name;
        Description = plugin.Description;
        VersionText = $"v{plugin.Version}";
        Glyph = plugin.Id == "media.youtube" ? "▶" : plugin.Name[..1].ToUpperInvariant();
        _isFavorite = isFavorite;
        OpenCommand = new RelayCommand(open);
        ToggleFavoriteCommand = new RelayCommand(() => toggleFavorite(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string VersionText { get; }
    public string Glyph { get; }
    public ICommand OpenCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand RemoveCommand { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value)) OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    public string FavoriteGlyph => _isFavorite ? "★" : "☆";
}
