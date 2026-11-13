using System.Windows;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.Views;

/// <summary>
/// Lists a remote ZIP's entries via HTTP Range. The user either picks entries (returned in <see cref="SelectedEntries"/>
/// for the New Download dialog to fetch) or asks for the whole archive (<see cref="StartDownloadRequested"/>).
/// </summary>
public partial class ZipPreviewWindow : Window
{
    private readonly string _url;
    private CancellationTokenSource? _cts;
    private List<ZipEntryDisplayItem> _items = []; // files only; folder rows are built from their paths
    private List<ZipEntryDisplayItem> _roots = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<ZipEntryDisplayItem> _visible = [];
    private bool _selectionUiQueued;

    public bool StartDownloadRequested { get; private set; }
    public List<ZipEntryMetadata> SelectedEntries { get; private set; } = [];

    private readonly bool _isRar;
    private readonly string? _cookies;
    private readonly string? _referrer;

    /// <param name="fileName">Name the browser reported; decides ZIP vs RAR when the URL has no extension.</param>
    /// <param name="cookies">Sent with RAR range requests (sites behind a login, e.g. Moodle).</param>
    public ZipPreviewWindow(string url, IEnumerable<ZipEntryMetadata>? preselected = null, string? fileName = null,
        string? cookies = null, string? referrer = null)
    {
        InitializeComponent();
        _url = url;
        _isRar = RemoteRarArchive.IsRarName(fileName) || RemoteRarArchive.IsRarName(url);
        _cookies = cookies;
        _referrer = referrer;
        if (_isRar)
        {
            Title = "RAR Arşiv Önizleme - VortexDownloader";
            HeaderTitleText.Text = "RAR arşiv içeriği";
        }
        _preselected = preselected?.Select(e => e.FileName).ToHashSet(StringComparer.Ordinal) ?? [];
        UrlTextBlock.Text = url;
        Loaded += (_, _) => _ = LoadZipEntriesAsync();
        Closed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); _cts = null; };
    }

    private readonly HashSet<string> _preselected;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
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
            List<ZipEntryMetadata> entries;
            if (_isRar)
            {
                var listing = await RemoteRarArchive.ListAsync(_url, _cookies, _referrer, _cts.Token);
                entries = listing.Entries;
                if (listing.IsSolid)
                    FooterHintText.Text = "Bu RAR katı (solid) sıkıştırılmış: seçtiğin dosyaya kadar arşivin baştan o noktaya kadarki kısmı da indirilir.";
            }
            else
            {
                entries = await ZipInspector.InspectRemoteZipAsync(_url, _cts.Token);
            }

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
                Entry = entry,
                IsChecked = !entry.IsDirectory && _preselected.Contains(entry.FileName)
            }).ToList();

            _items = displayList.Where(i => !i.IsDirectory).ToList();
            foreach (var item in _items) item.PropertyChanged += (_, _) => QueueSelectionUi();
            _roots = ZipEntryDisplayItem.BuildTree(_items);
            // A single top-level folder (the usual "app/..." layout) is opened so the list isn't one lonely row.
            if (_roots is [{ IsDirectory: true } only]) only.IsExpanded = true;
            _visible.Clear();
            foreach (var n in ZipEntryDisplayItem.VisibleUnder(_roots)) _visible.Add(n);
            ZipEntriesDataGrid.ItemsSource = _visible;
            UpdateSelectionUi();

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
            ErrorMessageText.Text = $"{(_isRar ? "RAR" : "ZIP")} arşivi okunamadı:\n{ex.Message}";
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _ = LoadZipEntriesAsync();

    private void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        StartDownloadRequested = true;
        DialogResult = true;
    }

    private List<ZipEntryDisplayItem> CheckedItems => _items.Where(i => i.IsChecked && i.IsSelectable).ToList();

    // Ticking a folder flips many files at once; refresh the counters once, not once per file.
    private void QueueSelectionUi()
    {
        if (_selectionUiQueued) return;
        _selectionUiQueued = true;
        Dispatcher.BeginInvoke(() => { _selectionUiQueued = false; UpdateSelectionUi(); });
    }

    private void ToggleFolder(ZipEntryDisplayItem folder)
    {
        int idx = _visible.IndexOf(folder);
        if (idx < 0) return;
        if (folder.IsExpanded)
        {
            folder.IsExpanded = false;
            while (idx + 1 < _visible.Count && _visible[idx + 1].Depth > folder.Depth) _visible.RemoveAt(idx + 1);
        }
        else
        {
            folder.IsExpanded = true;
            int i = idx + 1;
            foreach (var n in ZipEntryDisplayItem.VisibleUnder(folder.Children)) _visible.Insert(i++, n);
        }
    }

    private void UpdateSelectionUi()
    {
        foreach (var row in _visible) if (row.IsDirectory) row.RaiseCheckState();
        var picked = CheckedItems;
        int selectable = _items.Count(i => i.IsSelectable);
        ExtractEntryButton.Content = picked.Count > 0
            ? $"Seçilenleri ekle ({picked.Count}) · {DownloadTask.FormatBytes(picked.Sum(i => i.UncompressedSize))}"
            : "Seçilenleri ekle";
        ExtractEntryButton.IsEnabled = picked.Count > 0;
        SelectAllCheckBox.IsEnabled = selectable > 0;
        SelectAllCheckBox.IsChecked = picked.Count == 0 ? false : picked.Count == selectable ? true : null;
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // Clicking from "all" clears; from none/partial selects everything
        bool selectAll = CheckedItems.Count < _items.Count(i => i.IsSelectable);
        foreach (var item in _items.Where(i => i.IsSelectable)) item.IsChecked = selectAll;
        UpdateSelectionUi();
    }

    // Clicking anywhere on a file row toggles its box (the box itself handles its own clicks)
    private void ZipEntriesDataGrid_RowClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindParent<System.Windows.Controls.CheckBox>(source) != null) return;
        if (FindParent<System.Windows.Controls.DataGridRow>(source)?.Item is not ZipEntryDisplayItem item) return;
        if (item.IsDirectory) ToggleFolder(item); // a folder row opens/closes; its box selects everything inside
        else if (item.IsSelectable) item.IsChecked = !item.IsChecked;
    }

    private void ZipEntriesDataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ZipEntriesDataGrid.SelectedItem is not ZipEntryDisplayItem item) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Space when item.IsSelectable:
                item.CheckState = item.CheckState != true;
                break;
            case System.Windows.Input.Key.Right when item is { IsDirectory: true, IsExpanded: false }:
            case System.Windows.Input.Key.Left when item is { IsDirectory: true, IsExpanded: true }:
                ToggleFolder(item);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    // Hands the picked entries back to the New Download dialog, which downloads them into its target folder
    private void ExtractEntryButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries = CheckedItems.Select(i => i.Entry).ToList();
        if (SelectedEntries.Count == 0) return;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public class ZipEntryDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
            PropertyChanged?.Invoke(this, new(nameof(CheckState)));
        }
    }

    // ---- Folder tree (rows are a flat list; Depth gives the indent) ----
    public int Depth { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<ZipEntryDisplayItem> Children { get; } = [];
    public System.Windows.Thickness Indent => new(Depth * 20, 0, 0, 0);

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new(nameof(Chevron)));
        }
    }
    public string Chevron => IsExpanded ? "" : "";

    private IEnumerable<ZipEntryDisplayItem> Files() =>
        Children.SelectMany(c => c.IsDirectory ? c.Files() : [c]);

    /// <summary>File: its own box. Folder: true/false when every file inside agrees, null when mixed; setting it ticks them all.</summary>
    public bool? CheckState
    {
        get
        {
            if (!IsDirectory) return IsChecked;
            int total = 0, on = 0;
            foreach (var f in Files()) { total++; if (f.IsChecked) on++; }
            return on == 0 ? false : on == total ? true : null;
        }
        set
        {
            if (!IsDirectory) { IsChecked = value == true; return; }
            foreach (var f in Files()) f.IsChecked = value == true;
        }
    }
    public void RaiseCheckState() => PropertyChanged?.Invoke(this, new(nameof(CheckState)));

    public bool IsSelectable => !IsDirectory || Children.Count > 0;

    /// <summary>Groups files into folder rows from their paths (folders first, then by name) and sums folder sizes.</summary>
    public static List<ZipEntryDisplayItem> BuildTree(IEnumerable<ZipEntryDisplayItem> files)
    {
        var roots = new List<ZipEntryDisplayItem>();
        var folders = new Dictionary<string, ZipEntryDisplayItem>(StringComparer.Ordinal);

        ZipEntryDisplayItem? Folder(string path)
        {
            if (path.Length == 0) return null;
            if (folders.TryGetValue(path, out var f)) return f;
            int cut = path.LastIndexOf('/');
            var parent = Folder(cut < 0 ? "" : path[..cut]);
            f = new ZipEntryDisplayItem { FileName = path + "/", DisplayName = path[(cut + 1)..], IsDirectory = true, Depth = parent == null ? 0 : parent.Depth + 1 };
            (parent?.Children ?? roots).Add(f);
            return folders[path] = f;
        }

        foreach (var file in files)
        {
            string path = file.FileName.Replace('\\', '/').Trim('/');
            int cut = path.LastIndexOf('/');
            var parent = Folder(cut < 0 ? "" : path[..cut]);
            file.DisplayName = path[(cut + 1)..];
            file.Depth = parent == null ? 0 : parent.Depth + 1;
            (parent?.Children ?? roots).Add(file);
        }

        void Finish(List<ZipEntryDisplayItem> list)
        {
            list.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            foreach (var f in list.Where(i => i.IsDirectory))
            {
                Finish(f.Children);
                var inside = f.Files().ToList();
                f.UncompressedSize = inside.Sum(i => i.UncompressedSize);
                f.CompressedSize = inside.Sum(i => i.CompressedSize);
                f.UncompressedSizeFormatted = DownloadTask.FormatBytes(f.UncompressedSize);
                f.CompressedSizeFormatted = DownloadTask.FormatBytes(f.CompressedSize);
                f.LastModifiedFormatted = $"{inside.Count:N0} dosya";
            }
        }
        Finish(roots);
        return roots;
    }

    /// <summary>The rows shown for these nodes: each node, plus the contents of folders that are open.</summary>
    public static IEnumerable<ZipEntryDisplayItem> VisibleUnder(IEnumerable<ZipEntryDisplayItem> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n.IsDirectory && n.IsExpanded)
                foreach (var c in VisibleUnder(n.Children)) yield return c;
        }
    }
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
