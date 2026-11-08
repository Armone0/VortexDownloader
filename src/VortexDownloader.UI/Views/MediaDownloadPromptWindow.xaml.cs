using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>
/// YouTube ve Web Medya İndirme Onay Penceresi (Seal &amp; Soft Loft Tasarımı).
/// yt-dlp ve FFmpeg motoru için format, çözünürlük, altyazı ve eşzamanlı parça parametrelerini yönetir.
/// </summary>
public partial class MediaDownloadPromptWindow : Window
{
    private readonly DownloadEngine? _engine;
    private string _currentDirectory;
    private bool _isInternalSelectionUpdating;
    private CancellationTokenSource? _thumbnailCts;
    private MediaMetadata? _lastMetadata;

    // Public Result Properties for Engine & ViewModels
    public string ResultUrl { get; private set; } = string.Empty;
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public string ResultTitle { get; private set; } = string.Empty;
    public string SelectedQuality { get; private set; } = "1080p";
    public string SelectedFormatId { get; private set; } = "1080p";
    public string Extension { get; private set; } = ".mp4";
    public int ConcurrentFragments { get; private set; } = 8;
    public bool DownloadSubtitles { get; private set; } = false;
    public bool EmbedMetadata { get; private set; } = true;
    public bool StartImmediately { get; private set; } = true;
    public bool IsAudioOnly { get; private set; } = false;

    public string? ChannelName { get; private set; }
    public string? DurationText { get; private set; }
    public string? ThumbnailUrl { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
            _thumbnailCts = null;
        }
        catch { }

        base.OnClosed(e);
    }

    public MediaDownloadPromptWindow(
        DownloadEngine? engine = null,
        string? initialUrl = null,
        string? defaultFolder = null)
    {
        InitializeComponent();
        _engine = engine;

        // Base destination directory
        _currentDirectory = !string.IsNullOrWhiteSpace(defaultFolder) && Directory.Exists(defaultFolder)
            ? defaultFolder
            : GetBaseDownloadDirectory();

        TxtDestPath.Text = _currentDirectory;

        // Initial fragments
        ConcurrentFragments = 8;
        SliderConcurrentFragments.Value = ConcurrentFragments;
        TxtFragmentCount.Text = ConcurrentFragments.ToString();

        // Kullanıcı linki kendisi yapıştıracak; hazır link konulmaz
        string urlToUse = !string.IsNullOrWhiteSpace(initialUrl) ? initialUrl.Trim() : string.Empty;

        TxtMediaUrl.Text = urlToUse;
        ResultUrl = urlToUse;

        Loaded += MediaDownloadPromptWindow_Loaded;
    }

    private void MediaDownloadPromptWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ResultUrl) && IsValidWebMediaUrl(ResultUrl))
        {
            AnalyzeMediaUrl(ResultUrl);
        }
        else
        {
            ApplyDefaultPlaceholders("Medya Bağlantısı Bekleniyor", "Web Medya", "--:--", null);
        }
    }

    public static bool IsValidWebMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com")
            || host.Contains("youtu.be")
            || host.Contains("vimeo.com")
            || host.Contains("tiktok.com")
            || host.Contains("twitter.com")
            || host.Contains("x.com")
            || host.Contains("instagram.com")
            || host.Contains("twitch.tv")
            || host.Contains("facebook.com")
            || host.Contains("dailymotion.com")
            || host.Contains("bilibili.com")
            || host.Contains("soundcloud.com");
    }

    private async void AnalyzeMediaUrl(string url)
    {
        TxtHeaderStatus.Text = "Video akış bilgileri taranıyor...";
        ThumbnailPlaceholder.Visibility = Visibility.Visible;

        // Extract possible title from video ID or URL structure if metadata fetch is in flight
        string inferredTitle = ExtractTitleFromUrl(url);
        ApplyDefaultPlaceholders(inferredTitle, "YouTube / Web Video", "Akış", null);

        // Try extracting YouTube thumbnail directly if it's a YouTube URL
        string? ytThumb = TryGetYouTubeThumbnail(url);
        if (!string.IsNullOrEmpty(ytThumb))
        {
            LoadThumbnailAsync(ytThumb);
        }

        // Try yt-dlp inspection if installed and available
        try
        {
            var sidecar = new SidecarManager();
            if (sidecar.GetYtDlpPath() != null)
            {
                TxtHeaderStatus.Text = "yt-dlp ile medya analizi yapılıyor...";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var service = new YtDlpService(sidecar);
                var meta = await service.InspectMediaAsync(url, cts.Token);
                if (IsLoaded)
                {
                    ApplyMetadata(meta);
                }
            }
        }
        catch
        {
            if (IsLoaded)
            {
                TxtHeaderStatus.Text = inferredTitle;
            }
        }
    }

    public void ApplyMetadata(MediaMetadata meta)
    {
        _lastMetadata = meta;
        ResultTitle = meta.Title;
        ChannelName = meta.Uploader;
        ThumbnailUrl = meta.ThumbnailUrl;

        string durationStr = meta.DurationSeconds > 0
            ? TimeSpan.FromSeconds(meta.DurationSeconds).ToString(@"mm\:ss")
            : "--:--";

        DurationText = durationStr;

        SetMediaInfo(meta.Title, meta.Uploader, durationStr, meta.ThumbnailUrl,
            $"{meta.AvailableFormats.Count} akış/format mevcut. Otomatik en iyi kalite eşleştirildi.");

        UpdateEstimatedSize();
    }

    public MediaDownloadOptions ToDownloadOptions()
    {
        return new MediaDownloadOptions
        {
            Url = ResultUrl,
            DestinationPath = ResultDestinationPath,
            SelectedFormatId = SelectedFormatId,
            ExtractAudioOnly = IsAudioOnly,
            AudioFormat = Extension.TrimStart('.').ToLowerInvariant(),
            ConcurrentFragments = ConcurrentFragments,
            DownloadSubtitles = DownloadSubtitles
        };
    }

    private static string ExtractTitleFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Host.Contains("youtube.com") || uri.Host.Contains("youtu.be"))
            {
                var match = Regex.Match(url, @"(?:v=|\/embed\/|\/watch\?v=|\/shorts\/|youtu\.be\/)([\w-]{11})");
                if (match.Success)
                {
                    return $"YouTube_Video_{match.Groups[1].Value}";
                }
            }
        }
        catch { }

        return "Vortex_Media_Download";
    }

    private static string? TryGetYouTubeThumbnail(string url)
    {
        try
        {
            var match = Regex.Match(url, @"(?:v=|\/embed\/|\/watch\?v=|\/shorts\/|youtu\.be\/)([\w-]{11})");
            if (match.Success)
            {
                string videoId = match.Groups[1].Value;
                // High quality YouTube thumbnail
                return $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
            }
        }
        catch { }

        return null;
    }

    public void SetMediaInfo(string title, string channel, string duration, string? thumbnailUrl, string? formatInfo = null)
    {
        ResultTitle = title;
        ChannelName = channel;
        DurationText = duration;
        ThumbnailUrl = thumbnailUrl;

        TxtMediaTitle.Text = title;
        TxtHeaderStatus.Text = title;
        TxtChannel.Text = $"👤 {channel}";
        TxtDuration.Text = $"⏱️ {duration}";
        TxtDurationBadge.Text = $"⏱️ {duration}";

        if (!string.IsNullOrWhiteSpace(formatInfo))
        {
            TxtStreamInfo.Text = formatInfo;
        }

        TxtFileName.Text = SanitizeFileName(title);
        UpdateDestinationPath();

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            LoadThumbnailAsync(thumbnailUrl);
        }
        else
        {
            ThumbnailPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ApplyDefaultPlaceholders(string title, string channel, string duration, string? thumbUrl)
    {
        SetMediaInfo(title, channel, duration, thumbUrl);
    }

    private async void LoadThumbnailAsync(string url)
    {
        try
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
        }
        catch { }

        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        try
        {
            ThumbnailPlaceholder.Visibility = Visibility.Visible;

            await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                if (!token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ThumbnailImage.Source = bitmap;
                        ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
                    });
                }
            }, token);
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                ThumbnailPlaceholder.Visibility = Visibility.Visible;
            }
        }
    }

    #region Window Drag & Navigation Safety
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                e.Handled = true;
            }
            catch { }
        }
    }

    private void CardBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            if (e.OriginalSource is DependencyObject depObj && IsInteractiveControl(depObj))
            {
                return;
            }

            try
            {
                DragMove();
                e.Handled = true;
            }
            catch { }
        }
    }

    private static bool IsInteractiveControl(DependencyObject? source)
    {
        if (source == null) return false;

        return FindVisualParent<ButtonBase>(source) != null
            || FindVisualParent<TextBox>(source) != null
            || FindVisualParent<PasswordBox>(source) != null
            || FindVisualParent<ComboBox>(source) != null
            || FindVisualParent<Slider>(source) != null
            || FindVisualParent<CheckBox>(source) != null
            || FindVisualParent<ScrollBar>(source) != null
            || FindVisualParent<Thumb>(source) != null;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
            {
                return parent;
            }

            try
            {
                if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
                {
                    child = VisualTreeHelper.GetParent(child);
                }
                else if (child is FrameworkContentElement fce)
                {
                    child = fce.Parent;
                }
                else
                {
                    child = LogicalTreeHelper.GetParent(child);
                }
            }
            catch
            {
                break;
            }
        }

        return null;
    }
    #endregion

    #region Controls & Form Events
    private void TxtMediaUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResultUrl = TxtMediaUrl.Text.Trim();
    }

    private void BtnAnalyzeMedia_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultUrl))
        {
            MessageBox.Show(this, "Lütfen geçerli bir YouTube veya medya bağlantısı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AnalyzeMediaUrl(ResultUrl);
    }

    private void CmbFormats_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalSelectionUpdating || CmbFormats.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        _isInternalSelectionUpdating = true;
        try
        {
            switch (tag.ToLowerInvariant())
            {
                case "2160p":
                    SelectedQuality = "4K";
                    SelectedFormatId = "2160p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "1440p":
                    SelectedQuality = "2K";
                    SelectedFormatId = "1440p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "1080p":
                    SelectedQuality = "1080p";
                    SelectedFormatId = "1080p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "720p":
                    SelectedQuality = "720p";
                    SelectedFormatId = "720p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "480p":
                    SelectedQuality = "480p";
                    SelectedFormatId = "480p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "360p":
                    SelectedQuality = "360p";
                    SelectedFormatId = "360p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "mp3":
                    SelectedQuality = "MP3";
                    SelectedFormatId = "bestaudio/best";
                    SetFormatExtension(".mp3", isAudio: true);
                    break;
                case "m4a":
                    SelectedQuality = "M4A";
                    SelectedFormatId = "bestaudio[ext=m4a]/bestaudio/best";
                    SetFormatExtension(".m4a", isAudio: true);
                    break;
                case "flac":
                    SelectedQuality = "FLAC";
                    SelectedFormatId = "bestaudio/best";
                    SetFormatExtension(".flac", isAudio: true);
                    break;
                case "opus":
                    SelectedQuality = "OPUS";
                    SelectedFormatId = "bestaudio[ext=opus]/bestaudio/best";
                    SetFormatExtension(".opus", isAudio: true);
                    break;
            }

            UpdateEstimatedSize();
        }
        finally
        {
            _isInternalSelectionUpdating = false;
        }
    }

    private void UpdateEstimatedSize()
    {
        if (TxtEstimatedSize == null) return;

        if (_lastMetadata != null)
        {
            var bytes = _lastMetadata.GetEstimatedBytes(SelectedQuality, IsAudioOnly);
            if (bytes > 0)
            {
                TxtEstimatedSize.Text = $"~{DownloadTask.FormatBytes(bytes)}";
                return;
            }
        }

        TxtEstimatedSize.Text = "-- MB";
    }

    private void SetFormatExtension(string ext, bool isAudio)
    {
        Extension = ext;
        IsAudioOnly = isAudio;
        if (TxtExtension != null)
        {
            TxtExtension.Text = ext;
        }

        // Adjust category directory automatically if destination was not manually customized
        string baseDir = GetBaseDownloadDirectory();
        _currentDirectory = isAudio
            ? Path.Combine(baseDir, "Music")
            : Path.Combine(baseDir, "Video");

        if (TxtDestPath != null)
        {
            TxtDestPath.Text = _currentDirectory;
        }

        UpdateDestinationPath();
    }

    private void SliderConcurrentFragments_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFragmentCount != null)
        {
            int val = (int)e.NewValue;
            TxtFragmentCount.Text = val.ToString();
            ConcurrentFragments = val;
        }
    }

    private void BtnChipFragment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int val))
        {
            SliderConcurrentFragments.Value = val;
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "VortexDownloader - Medya Klasörünü Seçin",
            InitialDirectory = _currentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            _currentDirectory = dialog.FolderName;
            TxtDestPath.Text = _currentDirectory;
            UpdateDestinationPath();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnQueue_Click(object sender, RoutedEventArgs e)
    {
        StartImmediately = false;
        ConfirmAndClose();
    }

    private void BtnDownloadNow_Click(object sender, RoutedEventArgs e)
    {
        StartImmediately = true;
        ConfirmAndClose();
    }

    private void ConfirmAndClose()
    {
        ResultUrl = TxtMediaUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(ResultUrl))
        {
            MessageBox.Show(this, "Geçerli bir medya bağlantısı (URL) girilmelidir.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpdateDestinationPath();
        if (string.IsNullOrWhiteSpace(ResultDestinationPath))
        {
            MessageBox.Show(this, "Geçerli bir hedef dosya yolu belirtilmelidir.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(ResultDestinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string testFile = Path.Combine(dir, $".vortex_perm_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Hedef klasöre yazma izni bulunmuyor:\n{ex.Message}\nLütfen farklı bir klasör seçin.", "Erişim Reddedildi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DownloadSubtitles = ChkDownloadSubtitles.IsChecked == true;
        EmbedMetadata = ChkEmbedMetadata.IsChecked == true;
        ConcurrentFragments = (int)SliderConcurrentFragments.Value;

        DialogResult = true;
        Close();
    }

    private void UpdateDestinationPath()
    {
        if (TxtFileName == null || string.IsNullOrEmpty(_currentDirectory)) return;

        string baseName = Path.GetFileNameWithoutExtension(TxtFileName.Text.Trim());
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "media_download";
        }

        baseName = SanitizeFileName(baseName);
        string fileName = $"{baseName}{Extension}";

        string currentDir = Path.GetFullPath(_currentDirectory);
        ResultDestinationPath = Path.Combine(currentDir, fileName);
    }

    private static string SanitizeFileName(string rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName)) return "media_download";
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = rawFileName;
        foreach (char c in invalid)
        {
            clean = clean.Replace(c, '_');
        }
        return clean.Trim();
    }

    private static string GetBaseDownloadDirectory()
    {
        string targetDir = AppSettings.Current.DefaultDownloadDirectory;
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = AppSettings.GetDefaultDownloadPath();
        }
        return targetDir;
    }
    #endregion
}
