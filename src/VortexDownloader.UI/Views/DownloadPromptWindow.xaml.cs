using System.IO;
using System.Windows;
using System.Windows.Input;
using VortexDownloader.Core.Engine;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

public partial class DownloadPromptWindow : Window
{
    private readonly DownloadEngine _engine;
    private string _currentDirectory;

    public string ResultUrl { get; private set; }
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public int ResultChunkCount { get; private set; } = 8;
    public bool StartImmediately { get; private set; } = true;
    public UrlMetadata? CachedMetadata { get; private set; }
    public string? Cookies { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

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
        Cookies = cookies;
        Referrer = referrer;
        UserAgent = userAgent;

        TxtUrl.Text = url;
        _currentDirectory = AppSettings.Current.DefaultDownloadDirectory;
        TxtDestPath.Text = _currentDirectory;

        if (!string.IsNullOrWhiteSpace(suggestedFileName))
        {
            TxtFileName.Text = suggestedFileName;
            TxtHeaderFileName.Text = suggestedFileName;
        }

        if (size.HasValue && size.Value > 0)
        {
            TxtFileSize.Text = FormatBytes(size.Value);
            // Preload metadata to prevent single-use token burn
            CachedMetadata = new UrlMetadata(
                url,
                suggestedFileName ?? "download.bin",
                size.Value,
                SupportsRange: true,
                ETag: null);
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
            var meta = await _engine.InspectUrlAsync(ResultUrl, CancellationToken.None, Cookies, Referrer, UserAgent);
            CachedMetadata = meta;

            if (string.IsNullOrWhiteSpace(TxtFileName.Text) || TxtFileName.Text == "download.bin")
            {
                TxtFileName.Text = meta.SuggestedFileName;
                TxtHeaderFileName.Text = meta.SuggestedFileName;
            }
            if (meta.ContentLength > 0)
            {
                TxtFileSize.Text = FormatBytes(meta.ContentLength);
            }
            else
            {
                TxtFileSize.Text = "Bilinmiyor (Akış)";
            }
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(TxtFileName.Text))
            {
                TxtFileName.Text = "download.bin";
                TxtHeaderFileName.Text = "download.bin";
            }
            TxtFileSize.Text = "Belirsiz";
        }

        UpdateDestinationPath();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
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

    private void TxtFileName_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        TxtHeaderFileName.Text = string.IsNullOrWhiteSpace(TxtFileName.Text) ? "İsimsiz İndirme" : TxtFileName.Text;
        UpdateDestinationPath();
    }

    private void SliderChunks_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtChunkCount != null)
        {
            int val = (int)e.NewValue;
            TxtChunkCount.Text = val.ToString();
            ResultChunkCount = val;
        }
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
        UpdateDestinationPath();
        if (string.IsNullOrWhiteSpace(ResultDestinationPath))
        {
            MessageBox.Show(this, "Geçerli bir hedef dosya yolu belirtilmelidir.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(_currentDirectory);
            string testFile = Path.Combine(_currentDirectory, $".vortex_perm_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Seçilen hedef klasöre yazma izni bulunmuyor:\n{ex.Message}\nLütfen farklı bir klasör seçin.", "Erişim Reddedildi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void UpdateDestinationPath()
    {
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

        string target = Path.GetFullPath(Path.Combine(_currentDirectory, fileName));
        string currentDir = Path.GetFullPath(_currentDirectory);
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
