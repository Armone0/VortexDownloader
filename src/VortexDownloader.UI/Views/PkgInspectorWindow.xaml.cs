using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Packages;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>
/// Read-only PS4 PKG viewer for a local file or a remote URL (read over HTTP Range before downloading).
/// Shows header / PARAM.SFO / icon0 and extracts unencrypted entries; nothing is ever decrypted.
/// </summary>
public partial class PkgInspectorWindow : Window
{
    private readonly string _source;
    private readonly bool _isRemote;
    private readonly string? _cookies;
    private readonly string? _referrer;
    private CancellationTokenSource? _cts;
    private List<PkgEntryDisplayItem> _items = [];
    private bool _extracting;

    /// <param name="pathOrUrl">Local .pkg path, or an http(s) URL.</param>
    /// <param name="cookies">Sent with range requests for remote sources (sites behind a login).</param>
    public PkgInspectorWindow(string pathOrUrl, string? cookies = null, string? referrer = null)
    {
        InitializeComponent();
        _source = pathOrUrl;
        _isRemote = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        _cookies = cookies;
        _referrer = referrer;
        SourceTextBlock.Text = pathOrUrl;
        ModeBadgeText.Text = _isRemote ? "İndirmeden önizleme" : "Salt okunur";
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        foreach (var item in _items) item.PropertyChanged -= Item_PropertyChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private CancellationToken RenewToken()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private async Task<Stream> OpenAsync(CancellationToken token) => _isRemote
        ? await Ps4PkgReader.OpenRemoteAsync(_source, _cookies, _referrer, token)
        : new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private async Task LoadAsync()
    {
        var token = RenewToken();
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;

        try
        {
            Ps4PkgInfo info;
            using (var stream = await OpenAsync(token))
                info = await Task.Run(() => Ps4PkgReader.Read(stream), token);
            ShowInfo(info);
            LoadingPanel.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Window closed while loading
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = ex.Message;
        }
    }

    private void ShowInfo(Ps4PkgInfo info)
    {
        var sfo = info.ParamSfo;
        TitleText.Text = sfo?.Title ?? "(başlık yok)";
        FieldsList.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("Başlık kimliği", sfo?.TitleId ?? "-"),
            new("İçerik kimliği", string.IsNullOrEmpty(info.ContentId) ? sfo?.ContentId ?? "-" : info.ContentId),
            new("Tür", Ps4PkgReader.DescribeCategory(sfo?.Category, info.ContentType)),
            new("Uygulama sürümü", sfo?.AppVersion ?? "-"),
            new("Gereken yazılım", sfo?.SystemVersion is { } fw ? $"{fw} ve üzeri" : "-"),
            new("PKG boyutu", DownloadTask.FormatBytes(info.FileLength)),
        };

        IconImage.Source = LoadIcon(info.Icon0);
        NoIconText.Visibility = IconImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _items) item.PropertyChanged -= Item_PropertyChanged;
        _items = info.Entries.Select(e => new PkgEntryDisplayItem(e)).ToList();
        foreach (var item in _items) item.PropertyChanged += Item_PropertyChanged;
        EntriesDataGrid.ItemsSource = _items;

        SfoDataGrid.ItemsSource = sfo?.Values;
        SfoExpander.Visibility = sfo == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionUi();
    }

    private static BitmapImage? LoadIcon(byte[]? png)
    {
        if (png == null) return null;
        try
        {
            using var ms = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // Not a decodable image: show the placeholder
        }
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateSelectionUi();

    private List<PkgEntryDisplayItem> CheckedItems => _items.Where(i => i.IsChecked && i.IsSelectable).ToList();

    private void UpdateSelectionUi()
    {
        var picked = CheckedItems;
        ExtractButton.Content = picked.Count > 0
            ? $"Seçilenleri çıkar ({picked.Count}) · {DownloadTask.FormatBytes(picked.Sum(i => i.Entry.Size))}"
            : "Seçilenleri çıkar";
        ExtractButton.IsEnabled = picked.Count > 0 && !_extracting;
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = CheckedItems.Select(i => i.Entry).ToList();
        if (picked.Count == 0) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dosyaların çıkarılacağı klasörü seçin",
            InitialDirectory = _isRemote ? "" : Path.GetDirectoryName(_source) ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        string folder = dialog.FolderName;

        var token = RenewToken();
        long total = picked.Sum(p => p.Size);
        var progress = new Progress<long>(done =>
            FooterHintText.Text = $"Çıkarılıyor... {DownloadTask.FormatBytes(done)} / {DownloadTask.FormatBytes(total)}");
        _extracting = true;
        UpdateSelectionUi();
        try
        {
            List<string> written;
            using (var stream = await OpenAsync(token))
                written = await Task.Run(() => Ps4PkgReader.ExtractEntries(stream, picked, folder, progress, token), token);
            FooterHintText.Text = $"{written.Count} dosya çıkarıldı: {folder}";
            try { Process.Start("explorer.exe", $"\"{folder}\""); } catch { /* opening the folder is a convenience */ }
        }
        catch (OperationCanceledException)
        {
            // Window closed while extracting
        }
        catch (Exception ex)
        {
            FooterHintText.Text = "Çıkarma başarısız.";
            MessageBox.Show(this, $"Dosyalar çıkarılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _extracting = false;
            if (IsLoaded) UpdateSelectionUi();
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class PkgEntryDisplayItem(Ps4PkgEntry entry) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        }
    }

    public Ps4PkgEntry Entry { get; } = entry;
    public bool IsSelectable => !Entry.IsEncrypted && Entry.Size > 0;
    public string Name => Entry.Name;
    public string IdText => $"0x{Entry.Id:X4}";
    public long Size => Entry.Size;
    public string SizeText => DownloadTask.FormatBytes(Entry.Size);
    public string StateText => Entry.IsEncrypted ? "şifreli" : "açık";
}
