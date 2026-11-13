using System.Windows;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>
/// Lists a remote ZIP's entries via HTTP Range. The user either picks entries (returned in <see cref="SelectedEntries"/>
/// for the New Download dialog to fetch) or asks for the whole archive (<see cref="StartDownloadRequested"/>).
/// </summary>
public partial class ZipPreviewWindow : Window
{
    private readonly string _url;
    private CancellationTokenSource? _cts;
    private List<ZipEntryDisplayItem> _items = [];

    public bool StartDownloadRequested { get; private set; }
    public List<ZipEntryMetadata> SelectedEntries { get; private set; } = [];

    private readonly bool _isRar;
    private readonly string? _cookies;
    private readonly string? _referrer;

    /// <param name="fileName">Name the browser reported; decides ZIP vs RAR when the URL has no extension.</param>
    /// <param name="cookies">Sent with RAR range requests (sites behind a login, e.g. Moodle).</param>
    public ZipPreviewWindow(string url, IEnumerable<ZipEntryMetadata>? preselected = null, string? fileName = null,
        string? cookies = null, string? referrer = null)
    {
        InitializeComponent();
        _url = url;
        _isRar = RemoteRarArchive.IsRarName(fileName) || RemoteRarArchive.IsRarName(url);
        _cookies = cookies;
        _referrer = referrer;
        if (_isRar)
        {
            Title = "RAR Arşiv Önizleme - VortexDownloader";
            HeaderTitleText.Text = "RAR arşiv içeriği";
        }
        _preselected = preselected?.Select(e => e.FileName).ToHashSet(StringComparer.Ordinal) ?? [];
        UrlTextBlock.Text = url;
        Loaded += (_, _) => _ = LoadZipEntriesAsync();
        Closed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); _cts = null; };
    }

    private readonly HashSet<string> _preselected;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private async Task LoadZipEntriesAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DataGridContainer.Visibility = Visibility.Collapsed;
        StartDownloadButton.IsEnabled = false;

        try
        {
            List<ZipEntryMetadata> entries;
            if (_isRar)
            {
                var listing = await RemoteRarArchive.ListAsync(_url, _cookies, _referrer, _cts.Token);
                entries = listing.Entries;
                if (listing.IsSolid)
                    FooterHintText.Text = "Bu RAR katı (solid) sıkıştırılmış: seçtiğin dosyaya kadar arşivin baştan o noktaya kadarki kısmı da indirilir.";
            }
            else
            {
                entries = await ZipInspector.InspectRemoteZipAsync(_url, _cts.Token);
            }

            var displayList = entries.Select(entry => new ZipEntryDisplayItem
            {
                FileName = entry.FileName,
                UncompressedSize = entry.UncompressedSize,
                UncompressedSizeFormatted = entry.IsDirectory ? "-" : DownloadTask.FormatBytes(entry.UncompressedSize),
                CompressedSize = entry.CompressedSize,
                CompressedSizeFormatted = entry.IsDirectory ? "-" : DownloadTask.FormatBytes(entry.CompressedSize),
                LastModified = entry.LastModified,
                LastModifiedFormatted = entry.LastModified > DateTime.MinValue
                    ? entry.LastModified.ToString("yyyy-MM-dd HH:mm")
                    : "-",
                IsDirectory = entry.IsDirectory,
                Entry = entry,
                IsChecked = !entry.IsDirectory && _preselected.Contains(entry.FileName)
            }).ToList();

            foreach (var item in displayList) item.PropertyChanged += (_, _) => UpdateSelectionUi();
            _items = displayList;
            ZipEntriesDataGrid.ItemsSource = displayList;
            UpdateSelectionUi();

            int fileCount = entries.Count(e => !e.IsDirectory);
            int dirCount = entries.Count(e => e.IsDirectory);
            long totalUncompressed = entries.Where(e => !e.IsDirectory).Sum(e => e.UncompressedSize);
            long totalCompressed = entries.Where(e => !e.IsDirectory).Sum(e => e.CompressedSize);

            FileCountSummaryText.Text = $"Toplam: {fileCount:N0} dosya" + (dirCount > 0 ? $", {dirCount:N0} klasör" : "");
            ArchiveSizeSummaryText.Text = $"Toplam Boyut: {DownloadTask.FormatBytes(totalUncompressed)} (Sıkıştırılmış: {DownloadTask.FormatBytes(totalCompressed)})";

            LoadingPanel.Visibility = Visibility.Collapsed;
            DataGridContainer.Visibility = Visibility.Visible;
            StartDownloadButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Dialog was closed before loading completed
        }
        catch (Exception ex)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorMessageText.Text = $"{(_isRar ? "RAR" : "ZIP")} arşivi okunamadı:\n{ex.Message}";
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadZipEntriesAsync();

    private void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        StartDownloadRequested = true;
        DialogResult = true;
    }

    private List<ZipEntryDisplayItem> CheckedItems => _items.Where(i => i.IsChecked && i.IsSelectable).ToList();

    private void UpdateSelectionUi()
    {
        var picked = CheckedItems;
        int selectable = _items.Count(i => i.IsSelectable);
        ExtractEntryButton.Content = picked.Count > 0
            ? $"Seçilenleri ekle ({picked.Count}) · {DownloadTask.FormatBytes(picked.Sum(i => i.UncompressedSize))}"
            : "Seçilenleri ekle";
        ExtractEntryButton.IsEnabled = picked.Count > 0;
        SelectAllCheckBox.IsEnabled = selectable > 0;
        SelectAllCheckBox.IsChecked = picked.Count == 0 ? false : picked.Count == selectable ? true : null;
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // Clicking from "all" clears; from none/partial selects everything
        bool selectAll = CheckedItems.Count < _items.Count(i => i.IsSelectable);
        foreach (var item in _items.Where(i => i.IsSelectable)) item.IsChecked = selectAll;
        UpdateSelectionUi();
    }

    // Clicking anywhere on a file row toggles its box (the box itself handles its own clicks)
    private void ZipEntriesDataGrid_RowClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindParent<System.Windows.Controls.CheckBox>(source) != null) return;
        if (FindParent<System.Windows.Controls.DataGridRow>(source)?.Item is ZipEntryDisplayItem { IsSelectable: true } item)
            item.IsChecked = !item.IsChecked;
    }

    private void ZipEntriesDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Space &&
            ZipEntriesDataGrid.SelectedItem is ZipEntryDisplayItem { IsSelectable: true } item)
        {
            item.IsChecked = !item.IsChecked;
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    // Hands the picked entries back to the New Download dialog, which downloads them into its target folder
    private void ExtractEntryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries = CheckedItems.Select(i => i.Entry).ToList();
        if (SelectedEntries.Count == 0) return;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public class ZipEntryDisplayItem : System.ComponentModel.INotifyPropertyChanged
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

    public bool IsSelectable => !IsDirectory;
    public string FileName { get; set; } = string.Empty;
    public long UncompressedSize { get; set; }
    public string UncompressedSizeFormatted { get; set; } = "-";
    public long CompressedSize { get; set; }
    public string CompressedSizeFormatted { get; set; } = "-";
    public DateTime LastModified { get; set; }
    public string LastModifiedFormatted { get; set; } = "-";
    public bool IsDirectory { get; set; }
    public ZipEntryMetadata Entry { get; set; } = new();
}
