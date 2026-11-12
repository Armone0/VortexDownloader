using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

public partial class NewDownloadDialog : Window
{
    private readonly DownloadEngine _engine;

    public string ResultUrl { get; private set; } = string.Empty;
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public int ResultChunkCount { get; private set; } = 8;
    public List<(string Url, string DestinationPath)> BatchItems { get; } = new();
    public bool IsBatchMode { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    public NewDownloadDialog(DownloadEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        ResultChunkCount = AppSettings.Current.DefaultChunkCount;
        FolderTextBox.Text = AppSettings.Current.DefaultDownloadDirectory;

        Loaded += NewDownloadDialog_Loaded;
    }

    private void NewDownloadDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                var urls = ParseUrls(text);
                if (urls.Count > 1)
                {
                    BatchModeCheckBox.IsChecked = true;
                    UrlTextBox.Text = text;
                }
                else if (urls.Count == 1)
                {
                    UrlTextBox.Text = urls[0];
                    _ = ProbeUrlAsync(urls[0]);
                }
            }
        }
        catch
        {
            // Ignore clipboard access issues
        }

        UrlTextBox.Focus();
    }

    private void BatchModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        bool isBatch = BatchModeCheckBox.IsChecked == true;
        IsBatchMode = isBatch;

        if (isBatch)
        {
            UrlTextBox.Height = 80;
            UrlTextBox.TextWrapping = TextWrapping.Wrap;
            FileNameTextBox.IsEnabled = false;
            FileNameTextBox.Text = "(Toplu modda dosya adları bağlantılardan otomatik alınır)";
            AnalyzeButton.IsEnabled = false;
            ZipPreviewButton.IsEnabled = false;
            UpdateBatchBadge();
        }
        else
        {
            UrlTextBox.Height = 36;
            UrlTextBox.TextWrapping = TextWrapping.NoWrap;
            FileNameTextBox.IsEnabled = true;
            AnalyzeButton.IsEnabled = true;
            ZipPreviewButton.IsEnabled = true;
            UpdateSingleUrlFileName();
            SizeInfoTextBlock.Text = "Boyut: Bilinmiyor";
            RangeInfoTextBlock.Text = "Range: Kontrol edilmedi";
        }
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string rawText = UrlTextBox.Text;
        var urls = ParseUrls(rawText);

        if (urls.Count > 1 && BatchModeCheckBox.IsChecked != true)
        {
            BatchModeCheckBox.IsChecked = true;
            return;
        }

        if (BatchModeCheckBox.IsChecked == true)
        {
            UpdateBatchBadge();
        }
        else
        {
            UpdateSingleUrlFileName();
        }
    }

    private void UpdateBatchBadge()
    {
        var urls = ParseUrls(UrlTextBox.Text);
        SizeInfoTextBlock.Text = urls.Count > 0
            ? $"{urls.Count} adet indirme bağlantısı algılandı"
            : "Toplu URL girilmedi";
        RangeInfoTextBlock.Text = "Toplu Kuyruk Modu";
    }

    private void UpdateSingleUrlFileName()
    {
        string url = UrlTextBox.Text.Trim();
        if ((string.IsNullOrWhiteSpace(FileNameTextBox.Text) || FileNameTextBox.Text == "(Toplu modda dosya adları bağlantılardan otomatik alınır)") && !string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var uri = new Uri(url);
                string segment = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    FileNameTextBox.Text = MainWindow.SanitizeFileName(Uri.UnescapeDataString(segment));
                }
            }
            catch
            {
                // Ignore
            }
        }
    }

    public static List<string> ParseUrls(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var separators = new[] { '\r', '\n', ',', ';' };
        var rawItems = input.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var validUrls = new List<string>();
        foreach (var item in rawItems)
        {
            if (Uri.TryCreate(item, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                validUrls.Add(uri.AbsoluteUri);
            }
        }
        return validUrls;
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Lütfen geçerli bir indirme URL'si girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ProbeUrlAsync(url);
    }

    private void ZipPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        var urls = ParseUrls(url);
        string targetUrl = urls.Count > 0 ? urls[0] : url;

        if (string.IsNullOrWhiteSpace(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "Lütfen geçerli bir HTTP veya HTTPS ZIP bağlantısı girin.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var preview = new ZipPreviewWindow(targetUrl, FolderTextBox.Text.Trim()) { Owner = this };
            if (preview.ShowDialog() == true && preview.StartDownloadRequested)
            {
                StartButton_Click(sender, e);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ZIP önizleme penceresi açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ProbeUrlAsync(string url)
    {
        AnalyzeButton.IsEnabled = false;
        AnalyzeButton.Content = "Taranıyor...";

        try
        {
            var meta = await _engine.InspectUrlAsync(url);
            if (!string.IsNullOrWhiteSpace(meta.SuggestedFileName) && (string.IsNullOrWhiteSpace(FileNameTextBox.Text) || FileNameTextBox.Text == "download.bin"))
            {
                FileNameTextBox.Text = MainWindow.SanitizeFileName(meta.SuggestedFileName);
            }

            if (meta.ContentLength > 0)
            {
                SizeInfoTextBlock.Text = $"Boyut: {DownloadTask.FormatBytes(meta.ContentLength)}";
            }
            else
            {
                SizeInfoTextBlock.Text = "Boyut: Belirsiz (Chunked)";
            }

            RangeInfoTextBlock.Text = meta.SupportsRange ? "Range: Var (Çok Parçalı)" : "Range: Yok (Tek Parça)";
        }
        catch
        {
            SizeInfoTextBlock.Text = "Boyut: Bilgi alınamadı";
            RangeInfoTextBlock.Text = "Sunucu yanıt vermedi";
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            AnalyzeButton.Content = "Bilgi Al";
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "İndirilecek Klasörü Seçin",
            InitialDirectory = FolderTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderTextBox.Text = dialog.FolderName;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        string rawText = UrlTextBox.Text;
        var urls = ParseUrls(rawText);

        if (urls.Count == 0)
        {
            MessageBox.Show(this, "Lütfen en az bir geçerli HTTP veya HTTPS indirme bağlantısı girin.", "Geçersiz URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string folder = FolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Hedef klasör oluşturulamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        try
        {
            string testFile = Path.Combine(folder, $".vortex_perm_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Seçilen hedef klasöre yazma izni bulunmuyor:\n{ex.Message}\nLütfen farklı bir klasör seçin.", "Erişim Reddedildi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int chunkCount = 8;
        if (ChunkCountComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int parsed))
        {
            chunkCount = parsed;
        }
        ResultChunkCount = chunkCount;
        BatchItems.Clear();

        if (urls.Count > 1 || BatchModeCheckBox.IsChecked == true)
        {
            IsBatchMode = true;
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < urls.Count; i++)
            {
                string u = urls[i];
                string candidateName = "download.bin";
                try
                {
                    var uri = new Uri(u);
                    string seg = Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(seg))
                    {
                        candidateName = MainWindow.SanitizeFileName(Uri.UnescapeDataString(seg));
                    }
                }
                catch { }

                string nameWithoutExt = Path.GetFileNameWithoutExtension(candidateName);
                string ext = Path.GetExtension(candidateName);
                string targetPath = Path.Combine(folder, candidateName);
                int dupCounter = 1;
                while (usedPaths.Contains(targetPath) || File.Exists(targetPath))
                {
                    targetPath = Path.Combine(folder, $"{nameWithoutExt} ({dupCounter++}){ext}");
                }
                usedPaths.Add(targetPath);
                BatchItems.Add((u, targetPath));
            }

            ResultUrl = BatchItems[0].Url;
            ResultDestinationPath = BatchItems[0].DestinationPath;
        }
        else
        {
            IsBatchMode = false;
            string fileName = FileNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName) || fileName == "(Toplu modda dosya adları bağlantılardan otomatik alınır)")
            {
                fileName = "download.bin";
            }
            fileName = MainWindow.SanitizeFileName(fileName);

            ResultUrl = urls[0];
            ResultDestinationPath = Path.Combine(folder, fileName);
            BatchItems.Add((ResultUrl, ResultDestinationPath));
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
