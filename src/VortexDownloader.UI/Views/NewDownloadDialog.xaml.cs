using System.IO;
using System.Windows;
using System.Windows.Controls;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;

namespace VortexDownloader.UI.Views;

public partial class NewDownloadDialog : Window
{
    private readonly DownloadEngine _engine;

    public string ResultUrl { get; private set; } = string.Empty;
    public string ResultDestinationPath { get; private set; } = string.Empty;
    public int ResultChunkCount { get; private set; } = 8;

    public NewDownloadDialog(DownloadEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        // Set default download directory
        string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(userDownloads))
        {
            userDownloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        FolderTextBox.Text = userDownloads;

        Loaded += NewDownloadDialog_Loaded;
    }

    private void NewDownloadDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    UrlTextBox.Text = text;
                    _ = ProbeUrlAsync(text);
                }
            }
        }
        catch
        {
            // Ignore clipboard access issues
        }

        UrlTextBox.Focus();
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(FileNameTextBox.Text) && !string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var uri = new Uri(url);
                string segment = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    FileNameTextBox.Text = Uri.UnescapeDataString(segment);
                }
            }
            catch
            {
                // Ignore
            }
        }
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

    private async Task ProbeUrlAsync(string url)
    {
        AnalyzeButton.IsEnabled = false;
        AnalyzeButton.Content = "Taranıyor...";

        try
        {
            var meta = await _engine.InspectUrlAsync(url);
            if (!string.IsNullOrWhiteSpace(meta.SuggestedFileName) && (string.IsNullOrWhiteSpace(FileNameTextBox.Text) || FileNameTextBox.Text == "download.bin"))
            {
                FileNameTextBox.Text = meta.SuggestedFileName;
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
        string url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Lütfen geçerli bir HTTP veya HTTPS bağlantısı girin.", "Geçersiz URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string fileName = FileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download.bin";
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

        int chunkCount = 8;
        if (ChunkCountComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int parsed))
        {
            chunkCount = parsed;
        }

        ResultUrl = url;
        ResultDestinationPath = Path.Combine(folder, fileName);
        ResultChunkCount = chunkCount;

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
