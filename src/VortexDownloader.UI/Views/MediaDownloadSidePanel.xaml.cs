using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Plugins;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>
/// Ana pencereye entegre, sağa doğru açılan YouTube &amp; Medya İndirici yan sekmesi (Side Panel).
/// Yüksek çözünürlük, ClearType metin optimizasyonu ve Seal tarzı format hapları içerir.
/// </summary>
public partial class MediaDownloadSidePanel : UserControl
{
    private DownloadEngine? _engine;
    private IMediaProviderPlugin? _provider;
    private string _currentDirectory;
    private bool _isInternalSelectionUpdating;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _analyzeCts;
    private static readonly HttpClient ThumbnailClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    public event Action<MediaDownloadOptions, bool>? DownloadRequested;
    private bool _isClosing;

    public string ResultUrl { get; private set; } = string.Empty;
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public string ResultTitle { get; private set; } = string.Empty;
    public string SelectedQuality { get; private set; } = "1080p";
    public string SelectedFormatId { get; private set; } = "bestvideo[height<=1080]+bestaudio/best[height<=1080]";
    public string Extension { get; private set; } = ".mp4";
    public int ConcurrentFragments { get; private set; } = 8;
    public bool DownloadSubtitles { get; private set; } = false;
    public bool EmbedMetadata { get; private set; } = true;
    public bool IsAudioOnly { get; private set; } = false;

    public string? ChannelName { get; private set; }
    public string? DurationText { get; private set; }
    public string? ThumbnailUrl { get; private set; }

    public MediaDownloadSidePanel()
    {
        InitializeComponent();
        RenderTransform = new System.Windows.Media.TranslateTransform(20, 0);
        Opacity = 0;

        _currentDirectory = GetBaseDownloadDirectory();
        TxtDestPath.Text = _currentDirectory;

        ConcurrentFragments = 8;
        SliderConcurrentFragments.Value = ConcurrentFragments;
        TxtFragmentCount.Text = ConcurrentFragments.ToString();

    }

    public void OpenWithAnimation() { _isClosing = false; Visibility = Visibility.Visible; Opacity = 0; if (RenderTransform is System.Windows.Media.TranslateTransform t) { t.X = 22; t.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }); } BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(190))); FocusUrlInput(); }
    public async void CloseWithAnimation() { if (_isClosing || Visibility != Visibility.Visible) return; _isClosing = true; if (RenderTransform is System.Windows.Media.TranslateTransform t) t.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(24, TimeSpan.FromMilliseconds(180))); BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))); await Task.Delay(190); Visibility = Visibility.Collapsed; _isClosing = false; }

    public void Initialize(DownloadEngine engine, IMediaProviderPlugin? provider = null)
    {
        _engine = engine;
        _provider = provider;
    }

    public void CheckClipboardAndPopulate()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (IsValidWebMediaUrl(text))
                {
                    TxtMediaUrl.Text = text;
                    ResultUrl = text;
                    AnalyzeMediaUrl(text);
                    return;
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(TxtMediaUrl.Text) || TxtMediaUrl.Text.Contains("..."))
        {
            ApplyDefaultPlaceholders("Bilinmeyen Medya Akışı", "Web Medya", "--:--", null);
        }
    }

    public void FocusUrlInput()
    {
        TxtMediaUrl.Focus();
        TxtMediaUrl.SelectAll();
    }

    public void SetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsSupportedUrl(url)) return;
        TxtMediaUrl.Text = url;
        ResultUrl = url;
        AnalyzeMediaUrl(url);
    }

    private bool IsSupportedUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return _provider?.CanHandle(uri) ?? IsValidWebMediaUrl(url);
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

    public async void AnalyzeMediaUrl(string url)
    {
        _analyzeCts?.Cancel();
        _analyzeCts = new CancellationTokenSource();
        var ct = _analyzeCts.Token;

        TxtHeaderStatus.Text = "Video akış bilgileri taranıyor...";
        ThumbnailPlaceholder.Visibility = Visibility.Visible;

        string inferredTitle = ExtractTitleFromUrl(url);
        ApplyDefaultPlaceholders(inferredTitle, "YouTube / Web Video", "Akış", null);

        string? ytThumb = TryGetYouTubeThumbnail(url);
        if (!string.IsNullOrEmpty(ytThumb))
        {
            LoadThumbnailAsync(ytThumb);
        }

        try
        {
            var sidecar = new SidecarManager();
            if (sidecar.GetYtDlpPath() != null)
            {
                TxtHeaderStatus.Text = "yt-dlp ile çözümleniyor...";
                var service = new YtDlpService(sidecar);
                var meta = await service.InspectMediaAsync(url, ct);
                if (!ct.IsCancellationRequested && IsLoaded)
                {
                    ApplyMetadata(meta);
                }
            }
        }
        catch
        {
            if (!ct.IsCancellationRequested && IsLoaded)
            {
                TxtHeaderStatus.Text = inferredTitle;
            }
        }
    }

    public void ApplyMetadata(MediaMetadata meta)
    {
        ResultTitle = meta.Title;
        ChannelName = meta.Uploader;
        ThumbnailUrl = meta.ThumbnailUrl;

        string durationStr = meta.DurationSeconds > 0
            ? TimeSpan.FromSeconds(meta.DurationSeconds).ToString(@"mm\:ss")
            : "--:--";

        DurationText = durationStr;

        int maxHeight = meta.GetMaximumVideoHeight();
        TxtQualityAvailability.Text = maxHeight >= 2160 ? "Bu kaynakta 4K (2160p) mevcut; 4K seçimi bunu hedefler." : maxHeight > 0 ? $"Bu kaynakta 4K yok; en yüksek kalite {maxHeight}p." : "Kaynak çözünürlüğü belirlenemedi.";
        SetMediaInfo(meta.Title, meta.Uploader, durationStr, meta.ThumbnailUrl,
            $"{meta.AvailableFormats.Count} akış/format tespit edildi. En iyi kalite eşleştirildi.");
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
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        try
        {
            ThumbnailPlaceholder.Visibility = Visibility.Visible;

            var imageBytes = await ThumbnailClient.GetByteArrayAsync(url, token);
            if (token.IsCancellationRequested) return;
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(imageBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 640;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            ThumbnailImage.Source = bitmap;
            ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                ThumbnailPlaceholder.Visibility = Visibility.Visible;
            }
        }
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
                return $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
            }
        }
        catch { }

        return null;
    }

    private void TxtMediaUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResultUrl = TxtMediaUrl.Text.Trim();
    }

    private void BtnAnalyzeMedia_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSupportedUrl(ResultUrl))
        {
            MessageBox.Show("Desteklenen bir YouTube bağlantısı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AnalyzeMediaUrl(ResultUrl);
    }

    private void QualityRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInternalSelectionUpdating) return;

        _isInternalSelectionUpdating = true;
        try
        {
            if (Radio4K.IsChecked == true)
            {
                SelectedQuality = "4K";
                SelectedFormatId = "2160p";
                SetFormatExtension(".mp4", isAudio: false);
                SelectComboBoxByTag("2160p");
            }
            else if (Radio1080p.IsChecked == true)
            {
                SelectedQuality = "1080p";
                SelectedFormatId = "1080p";
                SetFormatExtension(".mp4", isAudio: false);
                SelectComboBoxByTag("1080p");
            }
            else if (Radio720p.IsChecked == true)
            {
                SelectedQuality = "720p";
                SelectedFormatId = "720p";
                SetFormatExtension(".mp4", isAudio: false);
                SelectComboBoxByTag("720p");
            }
            else if (RadioMP3.IsChecked == true)
            {
                SelectedQuality = "MP3";
                SelectedFormatId = "bestaudio/best";
                SetFormatExtension(".mp3", isAudio: true);
                SelectComboBoxByTag("mp3");
            }
        }
        finally
        {
            _isInternalSelectionUpdating = false;
        }
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
                    Radio4K.IsChecked = true;
                    SelectedQuality = "4K";
                    SelectedFormatId = "2160p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "1440p":
                    Radio4K.IsChecked = true;
                    SelectedQuality = "2K";
                    SelectedFormatId = "1440p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "1080p":
                    Radio1080p.IsChecked = true;
                    SelectedQuality = "1080p";
                    SelectedFormatId = "1080p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "720p":
                    Radio720p.IsChecked = true;
                    SelectedQuality = "720p";
                    SelectedFormatId = "720p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "480p":
                    ClearRadioSelection();
                    SelectedQuality = "480p";
                    SelectedFormatId = "480p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "360p":
                    ClearRadioSelection();
                    SelectedQuality = "360p";
                    SelectedFormatId = "360p";
                    SetFormatExtension(".mp4", isAudio: false);
                    break;
                case "mp3":
                    RadioMP3.IsChecked = true;
                    SelectedQuality = "MP3";
                    SelectedFormatId = "bestaudio/best";
                    SetFormatExtension(".mp3", isAudio: true);
                    break;
                case "m4a":
                    RadioMP3.IsChecked = true;
                    SelectedQuality = "M4A";
                    SelectedFormatId = "bestaudio[ext=m4a]/bestaudio/best";
                    SetFormatExtension(".m4a", isAudio: true);
                    break;
                case "flac":
                    RadioMP3.IsChecked = true;
                    SelectedQuality = "FLAC";
                    SelectedFormatId = "bestaudio/best";
                    SetFormatExtension(".flac", isAudio: true);
                    break;
                case "opus":
                    RadioMP3.IsChecked = true;
                    SelectedQuality = "OPUS";
                    SelectedFormatId = "bestaudio[ext=opus]/bestaudio/best";
                    SetFormatExtension(".opus", isAudio: true);
                    break;
            }
        }
        finally
        {
            _isInternalSelectionUpdating = false;
        }
    }

    private void SelectComboBoxByTag(string tag)
    {
        if (CmbFormats == null) return;

        foreach (var item in CmbFormats.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is string itemTag &&
                itemTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                CmbFormats.SelectedItem = cbi;
                break;
            }
        }
    }

    private void ClearRadioSelection()
    {
        Radio4K.IsChecked = false;
        Radio1080p.IsChecked = false;
        Radio720p.IsChecked = false;
        RadioMP3.IsChecked = false;
    }

    private void SetFormatExtension(string ext, bool isAudio)
    {
        Extension = ext;
        IsAudioOnly = isAudio;
        if (TxtExtension != null)
        {
            TxtExtension.Text = ext;
        }

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

        if (dialog.ShowDialog() == true)
        {
            _currentDirectory = dialog.FolderName;
            TxtDestPath.Text = _currentDirectory;
            UpdateDestinationPath();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        CloseWithAnimation();
    }

    private void BtnQueue_Click(object sender, RoutedEventArgs e)
    {
        ConfirmDownload(startImmediately: false);
    }

    private void BtnDownloadNow_Click(object sender, RoutedEventArgs e)
    {
        ConfirmDownload(startImmediately: true);
    }

    private void ConfirmDownload(bool startImmediately)
    {
        ResultUrl = TxtMediaUrl.Text.Trim();
        if (!IsSupportedUrl(ResultUrl))
        {
            MessageBox.Show("Desteklenen bir YouTube bağlantısı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UpdateDestinationPath();
        if (string.IsNullOrWhiteSpace(ResultDestinationPath))
        {
            MessageBox.Show("Geçerli bir hedef dosya yolu belirtilmelidir.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show($"Hedef klasöre yazma izni bulunmuyor:\n{ex.Message}\nLütfen farklı bir klasör seçin.", "Erişim Reddedildi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DownloadSubtitles = ChkDownloadSubtitles.IsChecked == true;
        EmbedMetadata = ChkEmbedMetadata.IsChecked == true;
        ConcurrentFragments = (int)SliderConcurrentFragments.Value;

        var options = new MediaDownloadOptions
        {
            Url = ResultUrl,
            DestinationPath = ResultDestinationPath,
            SelectedFormatId = SelectedFormatId,
            ExtractAudioOnly = IsAudioOnly,
            AudioFormat = Extension.TrimStart('.').ToLowerInvariant(),
            ConcurrentFragments = ConcurrentFragments,
            DownloadSubtitles = DownloadSubtitles
        };

        DownloadRequested?.Invoke(options, startImmediately);
        if (startImmediately) CloseWithAnimation();
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
}
