using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Network;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

public partial class ZipPreviewWindow : Window
{
    private readonly string _url;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _extractCts;
    private string _targetFolder;

    public bool StartDownloadRequested { get; private set; }

    public ZipPreviewWindow(string url, string? defaultFolder = null)
    {
        InitializeComponent();
        _url = url;
        _targetFolder = string.IsNullOrWhiteSpace(defaultFolder) ? AppSettings.Current.DefaultDownloadDirectory : defaultFolder;
        UrlTextBlock.Text = url;
        Loaded += ZipPreviewWindow_Loaded;
        Closed += ZipPreviewWindow_Closed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private void ZipPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = LoadZipEntriesAsync();
    }

    private void ZipPreviewWindow_Closed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _extractCts?.Cancel();
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
            List<ZipEntryMetadata> entries = await ZipInspector.InspectRemoteZipAsync(_url, _cts.Token);

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
                Entry = entry
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
            ErrorMessageText.Text = $"ZIP arşivi okunamadı:\n{ex.Message}";
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadZipEntriesAsync();
    }

    private void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        StartDownloadRequested = true;
        DialogResult = true;
        Close();
    }

    private List<ZipEntryDisplayItem> _items = [];

    private List<ZipEntryDisplayItem> CheckedItems => _items.Where(i => i.IsChecked && i.IsSelectable).ToList();

    private void UpdateSelectionUi()
    {
        var picked = CheckedItems;
        int selectable = _items.Count(i => i.IsSelectable);
        ExtractEntryButton.Content = picked.Count > 0
            ? $"Seçilenleri indir ({picked.Count}) · {DownloadTask.FormatBytes(picked.Sum(i => i.UncompressedSize))}"
            : "Seçilenleri indir";
        ExtractEntryButton.IsEnabled = _extractCts == null && picked.Count > 0;
        SelectAllCheckBox.IsEnabled = selectable > 0 && _extractCts == null;
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
        if (_extractCts != null || e.OriginalSource is not DependencyObject source) return;
        if (FindParent<System.Windows.Controls.CheckBox>(source) != null) return;
        if (FindParent<System.Windows.Controls.DataGridRow>(source)?.Item is ZipEntryDisplayItem { IsSelectable: true } item)
            item.IsChecked = !item.IsChecked;
    }

    private void ZipEntriesDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Space && _extractCts == null &&
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

    private async void ExtractEntryButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = CheckedItems;
        if (picked.Count == 0 || _extractCts != null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = picked.Count == 1 ? "Dosyanın kaydedileceği klasörü seçin" : $"{picked.Count} dosyanın kaydedileceği klasörü seçin",
            InitialDirectory = Directory.Exists(_targetFolder) ? _targetFolder : AppSettings.Current.DefaultDownloadDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        _targetFolder = dialog.FolderName;

        _extractCts = new CancellationTokenSource();
        UpdateSelectionUi();
        CancelExtractButton.Visibility = Visibility.Visible;
        ExtractProgressBar.Visibility = Visibility.Visible;
        ExtractProgressBar.Value = 0;
        ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        ExtractStatusText.ToolTip = null;

        // Overall progress is by bytes across all picked files; files run one after another
        long grandTotal = Math.Max(1, picked.Sum(i => i.Entry.UncompressedSize));
        long doneBefore = 0;
        var saved = new List<string>();
        var failed = new List<string>();
        bool cancelled = false;

        using var client = ChunkDownloader.CreateOptimizedHttpClient();
        for (int n = 0; n < picked.Count; n++)
        {
            var item = picked[n];
            string prefix = picked.Count > 1 ? $"{n + 1}/{picked.Count} · " : "";
            long total = item.Entry.UncompressedSize, before = doneBefore;
            ExtractStatusText.Text = $"İndiriliyor: {prefix}{item.FileName}";
            var progress = new Progress<long>(written =>
            {
                ExtractProgressBar.Value = (double)(before + written) / grandTotal;
                ExtractStatusText.Text = $"İndiriliyor: {prefix}{item.FileName} — {DownloadTask.FormatBytes(written)} / {DownloadTask.FormatBytes(total)}";
            });

            try
            {
                saved.Add(await ZipEntryExtractor.ExtractRemoteEntryAsync(client, _url, item.Entry, _targetFolder, progress, _extractCts.Token));
                item.IsChecked = false;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                failed.Add($"{item.FileName}: {ex.Message}");
            }
            doneBefore += total;
        }

        if (failed.Count > 0)
        {
            ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
            ExtractStatusText.Text = saved.Count > 0
                ? $"{saved.Count} dosya kaydedildi, {failed.Count} dosya indirilemedi (ayrıntı için üzerine gel)."
                : $"İndirilemedi: {failed[0]}";
            ExtractStatusText.ToolTip = string.Join("\n", failed);
        }
        else if (cancelled)
        {
            ExtractStatusText.Text = saved.Count > 0 ? $"İptal edildi; {saved.Count} dosya kaydedilmişti." : "İndirme iptal edildi.";
        }
        else
        {
            ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentHighlightBrush");
            ExtractStatusText.Text = saved.Count == 1 ? $"Kaydedildi: {saved[0]}" : $"{saved.Count} dosya kaydedildi: {_targetFolder}";
            ExtractStatusText.ToolTip = string.Join("\n", saved);
        }

        _extractCts.Dispose();
        _extractCts = null;
        CancelExtractButton.Visibility = Visibility.Collapsed;
        ExtractProgressBar.Visibility = Visibility.Collapsed;
        UpdateSelectionUi();
    }

    private void CancelExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractCts?.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
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
    public string DisplayIcon => IsDirectory ? "📁" : "📄";
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
