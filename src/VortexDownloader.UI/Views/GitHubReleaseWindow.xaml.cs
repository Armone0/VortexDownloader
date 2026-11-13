using System.Globalization;
using System.Windows;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Sources;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>An asset picked in the release window; <see cref="ExpectedSha256"/> is GitHub's digest when it gave one.</summary>
public sealed record GitHubAssetChoice(string Name, string Url, string? ExpectedSha256);

public partial class GitHubReleaseWindow : Window
{
    private sealed class ReleaseItem(GitHubRelease r)
    {
        public GitHubRelease Release { get; } = r;
        public string Label => Release.Prerelease ? $"{Release.Name}  (ön sürüm)" : Release.Name;
    }

    private sealed class AssetRow(GitHubAsset a) : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public GitHubAsset Asset { get; } = a;
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }
        public string SizeText => DownloadTask.FormatBytes(Asset.Size);
        public string CountText => Asset.DownloadCount == 1 ? "1 indirme" : $"{Asset.DownloadCount:N0} indirme";
        public Visibility DigestVisibility => Asset.Sha256 != null ? Visibility.Visible : Visibility.Collapsed;
        public string ExtLabel
        {
            get
            {
                string ext = System.IO.Path.GetExtension(Asset.Name).TrimStart('.').ToUpperInvariant();
                return ext.Length is > 0 and <= 4 ? ext : "FILE";
            }
        }
    }

    // GitHub notes are Markdown; show them as readable plain text instead of raw ##/**/` marks.
    private static string PlainNotes(string md)
    {
        var lines = md.Replace("\r", "").Split('\n').Select(l =>
        {
            string t = System.Text.RegularExpressions.Regex.Replace(l, @"^\s{0,3}#{1,6}\s*", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^(\s*)[-*]\s+", "$1• ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return t.Replace("**", "").Replace("`", "");
        });
        return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
    }

    private readonly string _owner, _repo;
    private readonly string? _tag;
    private CancellationTokenSource? _cts;
    private List<GitHubRelease> _all = [];

    public List<GitHubAssetChoice> SelectedAssets { get; private set; } = [];

    public GitHubReleaseWindow(string owner, string repo, string? tag)
    {
        InitializeComponent();
        _owner = owner; _repo = repo; _tag = tag;
        RepoText.Text = $"{owner}/{repo}";
        Loaded += (_, _) => _ = LoadAsync();
        Closed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); _cts = null; };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private void ShowStatus(string text, bool loading, bool retry)
    {
        StatusPanel.Visibility = Visibility.Visible;
        AssetsPanel.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = text;
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ShowStatus("Sürümler okunuyor...", true, false);
        try
        {
            _all = (await new GitHubReleaseClient().GetReleasesAsync(_owner, _repo, ct)).ToList();
            if (_tag != null && _all.FirstOrDefault(r => r.Tag == _tag) is { Prerelease: true }) PreReleaseCheck.IsChecked = true;
            FillCombo(_tag);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (GitHubRateLimitException ex) { ShowStatus(ex.Message, false, false); }
        catch (GitHubRepoNotFoundException ex) { ShowStatus(ex.Message, false, false); }
        catch (Exception ex) { ShowStatus($"Sürümler alınamadı: {ex.Message}", false, true); }
    }

    private void FillCombo(string? preferTag)
    {
        bool pre = PreReleaseCheck.IsChecked == true;
        var items = _all.Where(r => pre || !r.Prerelease).Select(r => new ReleaseItem(r)).ToList();
        ReleaseCombo.ItemsSource = items;
        ReleaseCombo.IsEnabled = items.Count > 0;
        if (items.Count == 0)
        {
            ShowStatus(_all.Count == 0 ? "Bu depoda yayınlanmış sürüm yok." : "Yalnızca ön sürümler var; \"Ön sürümleri göster\" kutusunu işaretle.", false, false);
            DateText.Text = "";
            return;
        }
        ReleaseCombo.SelectedItem = items.FirstOrDefault(i => i.Release.Tag == preferTag) ?? items[0];
    }

    private void PreReleaseCheck_Click(object sender, RoutedEventArgs e) =>
        FillCombo((ReleaseCombo.SelectedItem as ReleaseItem)?.Release.Tag);

    private void ReleaseCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReleaseCombo.SelectedItem is not ReleaseItem item) return;
        var r = item.Release;
        var facts = new List<string>();
        if (r.PublishedAt is { } d) facts.Add(d.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
        facts.Add(r.Tag);
        facts.Add($"{r.Assets.Count} dosya");
        DateText.Text = string.Join("  ·  ", facts);
        NotesText.Text = string.IsNullOrWhiteSpace(r.Body) ? "Bu sürüm için not yazılmamış." : PlainNotes(r.Body);

        if (r.Assets.Count == 0)
        {
            ShowStatus("Bu sürümde indirilebilir dosya yok.", false, false);
            AssetList.ItemsSource = null;
        }
        else
        {
            AssetList.ItemsSource = r.Assets.Select(a => new AssetRow(a)).ToList();
            StatusPanel.Visibility = Visibility.Collapsed;
            AssetsPanel.Visibility = Visibility.Visible;
        }
        UpdateSummary();
    }

    private void AssetCheck_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private void AssetRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The box handles its own clicks; anywhere else on the row toggles it.
        if (e.OriginalSource is DependencyObject src && FindParent<System.Windows.Controls.CheckBox>(src) != null) return;
        if ((sender as FrameworkElement)?.DataContext is AssetRow row) row.IsChecked = !row.IsChecked;
        UpdateSummary();
    }

    private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
    {
        var all = AssetList.ItemsSource as List<AssetRow> ?? [];
        bool select = all.Any(r => !r.IsChecked);
        foreach (var r in all) r.IsChecked = select;
        UpdateSummary();
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T) node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void UpdateSummary()
    {
        var all = AssetList.ItemsSource as List<AssetRow> ?? [];
        var rows = all.Where(r => r.IsChecked).ToList();
        SummaryText.Text = rows.Count == 0 ? "Seçili: 0" : $"Seçili: {rows.Count} · {DownloadTask.FormatBytes(rows.Sum(r => r.Asset.Size))}";
        DownloadButton.IsEnabled = rows.Count > 0;
        SelectAllCheck.IsChecked = rows.Count == 0 ? false : rows.Count == all.Count ? true : null;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAssets = (AssetList.ItemsSource as List<AssetRow> ?? [])
            .Where(r => r.IsChecked)
            .Select(r => new GitHubAssetChoice(r.Asset.Name, r.Asset.Url, r.Asset.Sha256))
            .ToList();
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
