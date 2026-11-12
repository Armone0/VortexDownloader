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

            ZipEntriesDataGrid.ItemsSource = displayList;

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

    private ZipEntryDisplayItem? SelectedFileItem =>
        ZipEntriesDataGrid.SelectedItem is ZipEntryDisplayItem { IsDirectory: false } item ? item : null;

    private void ZipEntriesDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ExtractEntryButton.IsEnabled = _extractCts == null && SelectedFileItem != null;
    }

    private void ZipEntriesDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only react to double-clicks on a row, not on headers or the scrollbar.
        if (e.OriginalSource is System.Windows.Media.Visual source &&
            FindParent<System.Windows.Controls.DataGridRow>(source) != null &&
            ExtractEntryButton.IsEnabled)
        {
            ExtractEntryButton_Click(sender, e);
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
        if (SelectedFileItem is not { } item || _extractCts != null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Dosyanın kaydedileceği klasörü seçin",
            InitialDirectory = Directory.Exists(_targetFolder) ? _targetFolder : AppSettings.Current.DefaultDownloadDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        _targetFolder = dialog.FolderName;

        _extractCts = new CancellationTokenSource();
        ExtractEntryButton.IsEnabled = false;
        CancelExtractButton.Visibility = Visibility.Visible;
        ExtractProgressBar.Visibility = Visibility.Visible;
        ExtractProgressBar.Value = 0;
        ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        ExtractStatusText.Text = $"İndiriliyor: {item.FileName}";

        long total = item.Entry.UncompressedSize;
        var progress = new Progress<long>(written =>
        {
            ExtractProgressBar.Value = total > 0 ? (double)written / total : 1;
            ExtractStatusText.Text = $"İndiriliyor: {item.FileName} — {DownloadTask.FormatBytes(written)} / {DownloadTask.FormatBytes(total)}";
        });

        try
        {
            using var client = ChunkDownloader.CreateOptimizedHttpClient();
            string path = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, _url, item.Entry, _targetFolder, progress, _extractCts.Token);
            ExtractProgressBar.Value = 1;
            ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentHighlightBrush");
            ExtractStatusText.Text = $"Kaydedildi: {path}";
            ExtractStatusText.ToolTip = path;
        }
        catch (OperationCanceledException)
        {
            ExtractStatusText.Text = "İndirme iptal edildi.";
        }
        catch (Exception ex)
        {
            ExtractStatusText.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrorBrush");
            ExtractStatusText.Text = $"İndirilemedi: {ex.Message}";
            ExtractStatusText.ToolTip = ex.Message;
        }
        finally
        {
            _extractCts.Dispose();
            _extractCts = null;
            CancelExtractButton.Visibility = Visibility.Collapsed;
            ExtractProgressBar.Visibility = Visibility.Collapsed;
            ExtractEntryButton.IsEnabled = SelectedFileItem != null;
        }
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

public class ZipEntryDisplayItem
{
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
