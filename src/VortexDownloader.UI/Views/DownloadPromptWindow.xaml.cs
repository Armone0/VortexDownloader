using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

public partial class DownloadPromptWindow : Window
{
    private readonly DownloadEngine _engine;
    private string _currentDirectory;
    private bool _isInternalCategoryUpdating;

    public string ResultUrl { get; private set; }
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public string ResultFileName => TxtFileName?.Text?.Trim() ?? string.Empty;
    public int ResultChunkCount { get; private set; } = 8;
    public bool StartImmediately { get; private set; } = true;
    public bool DownloadWithBrowser { get; private set; } = false;
    public UrlMetadata? CachedMetadata { get; private set; }
    public string? Cookies { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Description { get; private set; }
    public string? AuthUsername { get; private set; }
    public string? AuthPassword { get; private set; }
    /// <summary>Files picked inside the ZIP; when set, only these are fetched (into the folder of <see cref="ResultDestinationPath"/>).</summary>
    public List<ZipEntryMetadata>? ZipEntries { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    public DownloadPromptWindow(
        DownloadEngine engine,
        string url,
        string? suggestedFileName = null,
        long? size = null,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null)
    {
        InitializeComponent();
        _engine = engine;
        ResultUrl = url;
        ResultChunkCount = AppSettings.Current.DefaultChunkCount;
        SelectChunkChoice(ResultChunkCount);
        Cookies = cookies;
        Referrer = referrer;
        UserAgent = userAgent;

        TxtUrl.Text = url;
        _currentDirectory = GetBaseDownloadDirectory();
        TxtDestPath.Text = _currentDirectory;

        string initialName = !string.IsNullOrWhiteSpace(suggestedFileName) ? suggestedFileName : "download.bin";
        TxtFileName.Text = initialName;
        TxtHeaderFileName.Text = initialName;

        AutoSelectCategory(initialName);
        UpdateZipCard();

        if (size.HasValue && size.Value > 0)
        {
            TxtFileSize.Text = FormatBytes(size.Value);
            UpdateRangeBadge(true);
            CachedMetadata = new UrlMetadata(
                url,
                initialName,
                size.Value,
                SupportsRange: true,
                ETag: null);
        }
        else
        {
            UpdateRangeBadge(null);
        }

        Loaded += DownloadPromptWindow_Loaded;
    }

    private async void DownloadPromptWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // If metadata is already known from extension, skip extra probe to preserve single-use download links!
        if (CachedMetadata != null && CachedMetadata.ContentLength > 0)
        {
            UpdateDestinationPath();
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var meta = await _engine.InspectUrlAsync(ResultUrl, cts.Token, Cookies, Referrer, UserAgent);
            if (!IsLoaded) return;
            CachedMetadata = meta;

            if (string.IsNullOrWhiteSpace(TxtFileName.Text) || TxtFileName.Text == "download.bin")
            {
                TxtFileName.Text = meta.SuggestedFileName;
                TxtHeaderFileName.Text = meta.SuggestedFileName;
                AutoSelectCategory(meta.SuggestedFileName);
                UpdateZipCard();
            }
            if (meta.ContentLength > 0)
            {
                TxtFileSize.Text = FormatBytes(meta.ContentLength);
            }
            else
            {
                TxtFileSize.Text = "Bilinmiyor (Akış)";
            }

            UpdateRangeBadge(meta.SupportsRange);
        }
        catch
        {
            if (!IsLoaded) return;
            if (string.IsNullOrWhiteSpace(TxtFileName.Text))
            {
                TxtFileName.Text = "download.bin";
                TxtHeaderFileName.Text = "download.bin";
            }
            TxtFileSize.Text = "Belirsiz";
            UpdateRangeBadge(null);
        }

        if (!IsLoaded) return;
        UpdateDestinationPath();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                e.Handled = true;
            }
            catch
            {
            }
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
            catch
            {
            }
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

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "İndirilecek Klasörü Seçin",
            InitialDirectory = _currentDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            _currentDirectory = dialog.FolderName;
            TxtDestPath.Text = _currentDirectory;
            UpdateDestinationPath();
        }
    }

    private void TxtFileName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtHeaderFileName != null)
        {
            TxtHeaderFileName.Text = string.IsNullOrWhiteSpace(TxtFileName.Text) ? "İsimsiz İndirme" : TxtFileName.Text;
        }
        UpdateDestinationPath();
    }

    private void ChunkChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out int val))
            ResultChunkCount = val;
    }

    // Selects the choice closest to the saved default (settings may hold a value not offered here)
    private void SelectChunkChoice(int count)
    {
        var choices = ChunkChoices.Children.OfType<RadioButton>().ToList();
        var best = choices.MinBy(rb => Math.Abs(int.Parse((string)rb.Tag) - count));
        if (best != null) best.IsChecked = true;
    }

    private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInternalCategoryUpdating || CmbCategory == null || TxtDestPath == null) return;

        if (CmbCategory.SelectedItem is ComboBoxItem selected && selected.Tag is string tag)
        {
            string baseDir = GetBaseDownloadDirectory();
            _currentDirectory = GetCategoryDirectory(tag, baseDir);
            TxtDestPath.Text = _currentDirectory;
            UpdateDestinationPath();
        }
    }

    public static string DetectCategoryFromExtension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "General";
        string ext = Path.GetExtension(fileName).Trim().ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".iso" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" => "Compressed",
            ".exe" or ".msi" or ".apk" or ".bat" or ".cmd" or ".dmg" or ".pkg" or ".deb" or ".rpm" => "Programs",
            ".mp4" or ".mkv" or ".avi" or ".webm" or ".mov" or ".flv" or ".wmv" or ".m4v" or ".ts" => "Video",
            ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a" or ".wma" or ".opus" => "Music",
            ".pdf" or ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" or ".txt" or ".epub" or ".csv" => "Documents",
            _ => "General"
        };
    }

    private void AutoSelectCategory(string fileName)
    {
        string detectedTag = DetectCategoryFromExtension(fileName);
        _isInternalCategoryUpdating = true;
        try
        {
            if (CmbCategory != null)
            {
                foreach (var obj in CmbCategory.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is string tag &&
                        tag.Equals(detectedTag, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbCategory.SelectedItem = item;
                        string baseDir = GetBaseDownloadDirectory();
                        _currentDirectory = GetCategoryDirectory(tag, baseDir);
                        TxtDestPath.Text = _currentDirectory;
                        break;
                    }
                }
            }
        }
        finally
        {
            _isInternalCategoryUpdating = false;
        }
    }

    private static string GetCategoryDirectory(string tag, string baseDir)
    {
        return tag switch
        {
            "Compressed" => Path.Combine(baseDir, "Compressed"),
            "Programs" => Path.Combine(baseDir, "Programs"),
            "Video" => Path.Combine(baseDir, "Video"),
            "Music" => Path.Combine(baseDir, "Music"),
            "Documents" => Path.Combine(baseDir, "Documents"),
            _ => baseDir
        };
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

    private void UpdateRangeBadge(bool? supportsRange)
    {
        if (BadgeRange == null || TxtRangeSupport == null) return;

        if (supportsRange == true)
        {
            BadgeRange.Background = TryFindResource("BadgeBackgroundBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x2F, 0x36, 0x32));
            BadgeRange.BorderBrush = TryFindResource("AccentHighlightBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8E, 0xA1, 0x91));
            TxtRangeSupport.Text = "⚡ Range: Destekleniyor";
            TxtRangeSupport.Foreground = TryFindResource("AccentHighlightBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x8E, 0xA1, 0x91));
        }
        else if (supportsRange == false)
        {
            BadgeRange.Background = new SolidColorBrush(Color.FromArgb(50, 224, 177, 133));
            BadgeRange.BorderBrush = TryFindResource("StatusPausedBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE0, 0xB1, 0x85));
            TxtRangeSupport.Text = "⚠️ Range: Yok (Tek Parça)";
            TxtRangeSupport.Foreground = TryFindResource("StatusPausedBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xE0, 0xB1, 0x85));
        }
        else
        {
            BadgeRange.Background = TryFindResource("InputBackgroundBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x28));
            BadgeRange.BorderBrush = TryFindResource("CardBorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x38, 0x3D, 0x42));
            TxtRangeSupport.Text = "⏳ Range: Kontrol Ediliyor...";
            TxtRangeSupport.Foreground = TryFindResource("TextMutedBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x78));
        }
    }

    /// <summary>True when the picked entries come from a RAR (decided by the browser's file name or the URL path).</summary>
    public bool IsRar => RemoteRarArchive.IsRarName(TxtFileName.Text) || RemoteRarArchive.IsRarName(ResultUrl);

    private bool LooksLikeZip() =>
        TxtFileName.Text.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(ResultUrl, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    private void UpdateZipCard()
    {
        ZipCard.Visibility = LooksLikeZip() || IsRar || ZipEntries != null ? Visibility.Visible : Visibility.Collapsed;
        string kind = IsRar ? "RAR" : "ZIP";
        bool picked = ZipEntries is { Count: > 0 };
        if (ZipEntries is { Count: > 0 } entries)
        {
            long total = entries.Sum(z => z.UncompressedSize);
            TxtZipTitle.Text = $"🗜️ {kind} içinden {entries.Count} dosya seçildi · {FormatBytes(total)}";
            TxtZipHint.Text = "Yalnızca seçtiğin dosyalar iner, arşivin tamamı değil.";
            BtnZipPick.Content = "Seçimi değiştir";
            BtnDownloadNow.Content = $"⚡ Seçilenleri İndir ({entries.Count})";
        }
        else
        {
            TxtZipTitle.Text = $"🗜️ Bu bir {kind} arşivi";
            TxtZipHint.Text = "İçinden yalnızca istediğin dosyaları seçip indirebilirsin.";
            BtnZipPick.Content = "📦 İçeriği gör ve seç";
            BtnDownloadNow.Content = "⚡ Vortex ile İndir";
        }
        BtnZipClear.Visibility = picked ? Visibility.Visible : Visibility.Collapsed;
        // Picked entries download right away as cards; queueing / browser hand-off apply to the whole archive only
        BtnQueue.Visibility = picked ? Visibility.Collapsed : Visibility.Visible;
        BtnBrowser.Visibility = picked ? Visibility.Collapsed : Visibility.Visible;
        TxtFileName.IsEnabled = !picked;
    }

    private void BtnZipPick_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = new ZipPreviewWindow(ResultUrl, ZipEntries, TxtFileName.Text, Cookies, Referrer) { Owner = this };
            if (preview.ShowDialog() != true) return;
            if (preview.StartDownloadRequested)
            {
                ZipEntries = null;
                UpdateZipCard();
                BtnDownloadNow_Click(sender, e);
                return;
            }
            ZipEntries = preview.SelectedEntries.Count > 0 ? preview.SelectedEntries : null;
            UpdateZipCard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Arşiv içeriği açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnZipClear_Click(object sender, RoutedEventArgs e)
    {
        ZipEntries = null;
        UpdateZipCard();
    }

    private void BtnQueue_Click(object sender, RoutedEventArgs e)
    {
        DownloadWithBrowser = false;
        StartImmediately = false;
        ConfirmAndClose();
    }

    private void BtnDownloadNow_Click(object sender, RoutedEventArgs e)
    {
        DownloadWithBrowser = false;
        StartImmediately = true;
        ConfirmAndClose();
    }

    private void BtnDownloadWithBrowser_Click(object sender, RoutedEventArgs e)
    {
        DownloadWithBrowser = true;
        DialogResult = true;
        Close();
    }

    private void ConfirmAndClose()
    {
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
            MessageBox.Show(this, $"Seçilen hedef klasöre yazma izni bulunmuyor:\n{ex.Message}\nLütfen farklı bir klasör seçin.", "Erişim Reddedildi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ChkDontAskAgain.IsChecked == true)
        {
            AppSettings.Current.AutoStartBrowserDownloads = true;
            AppSettings.Current.Save();
        }

        Description = TxtDescription.Text.Trim();
        AuthUsername = TxtAuthUser.Text.Trim();
        AuthPassword = TxtAuthPassword.Password;

        if (!string.IsNullOrWhiteSpace(AuthUsername))
        {
            try
            {
                var uriB = new UriBuilder(ResultUrl);
                uriB.UserName = Uri.EscapeDataString(AuthUsername);
                if (!string.IsNullOrEmpty(AuthPassword))
                {
                    uriB.Password = Uri.EscapeDataString(AuthPassword);
                }
                ResultUrl = uriB.Uri.AbsoluteUri;
            }
            catch { }
        }

        DialogResult = true;
        Close();
    }

    private void UpdateDestinationPath()
    {
        if (TxtFileName == null || string.IsNullOrEmpty(_currentDirectory)) return;

        string fileName = Path.GetFileName(TxtFileName.Text.Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in invalid)
        {
            fileName = fileName.Replace(c, '_');
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
        }

        string currentDir = Path.GetFullPath(_currentDirectory);
        string target = Path.GetFullPath(Path.Combine(currentDir, fileName));
        if (!target.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
        {
            target = Path.Combine(currentDir, "download.bin");
        }
        ResultDestinationPath = target;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
