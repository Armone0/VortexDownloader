using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

public partial class ZipPreviewWindow : Window
{
    private readonly string _url;
    private CancellationTokenSource? _cts;

    public bool StartDownloadRequested { get; private set; }

    public ZipPreviewWindow(string url)
    {
        InitializeComponent();
        _url = url;
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
                IsDirectory = entry.IsDirectory
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
}
