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

    private sealed class AssetRow(GitHubAsset a)
    {
        public GitHubAsset Asset { get; } = a;
        public bool IsChecked { get; set; }
        public string SizeText => DownloadTask.FormatBytes(Asset.Size);
        public string CountText => $"{Asset.DownloadCount:N0} ↓";
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
            DateText.Text = ""; NotesExpander.Visibility = Visibility.Collapsed;
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
        DateText.Text = r.PublishedAt is { } d ? $"Yayın tarihi: {d.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}" : "";
        NotesText.Text = r.Body;
        NotesExpander.Visibility = string.IsNullOrWhiteSpace(r.Body) ? Visibility.Collapsed : Visibility.Visible;
        NotesExpander.IsExpanded = false;

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

    private void UpdateSummary()
    {
        var rows = (AssetList.ItemsSource as List<AssetRow>)?.Where(r => r.IsChecked).ToList() ?? [];
        SummaryText.Text = rows.Count == 0 ? "Seçili: 0" : $"Seçili: {rows.Count} · {DownloadTask.FormatBytes(rows.Sum(r => r.Asset.Size))}";
        DownloadButton.IsEnabled = rows.Count > 0;
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
