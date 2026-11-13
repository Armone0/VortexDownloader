using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.ViewModels;
using VortexDownloader.UI.Views;

namespace VortexDownloader.UI;

/// <summary>Multi-select download list and main-window keyboard shortcuts (kept apart from MainWindow.xaml.cs).</summary>
public partial class MainWindow
{
    /// <summary>Shown in Settings → Hakkında.</summary>
    public const string ShortcutsHelp =
        "Ctrl+N  Yeni indirme\n" +
        "Ctrl+V  Panodaki bağlantıyla yeni indirme\n" +
        "Del  Seçilenleri sil (onaylı)\n" +
        "Boşluk  Seçilenleri duraklat / devam ettir\n" +
        "Ctrl+A  Tümünü seç\n" +
        "Enter  Biten dosyayı aç\n" +
        "Ctrl / Shift + tık  Çoklu seçim";

    private ListCollectionView? _downloadsView;

    private void DownloadList_Loaded(object sender, RoutedEventArgs e)
    {
        if (_downloadsView != null) return;
        var vm = (MainViewModel)DataContext;
        // Live filtering re-evaluates single items when these change instead of rebuilding the list,
        // so selection and open popups survive the engine's 500 ms progress updates.
        _downloadsView = new ListCollectionView(vm.Downloads)
        {
            Filter = o => o is DownloadItemViewModel d && vm.MatchesView(d),
            IsLiveFiltering = true
        };
        _downloadsView.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.Status));
        _downloadsView.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.FileName));
        _downloadsView.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.Url));
        DownloadList.ItemsSource = _downloadsView;
        vm.PropertyChanged += OnViewFilterChanged;
    }

    private void OnViewFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentFilter) or nameof(MainViewModel.SearchText))
        {
            _downloadsView?.Refresh();
        }
    }

    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.SelectedItems = DownloadList.SelectedItems.Cast<DownloadItemViewModel>().ToList();
        vm.SelectedDownload = DownloadList.SelectedItem as DownloadItemViewModel;
    }

    private void MainWindow_FeaturesClosed(object? sender, EventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        vm.PropertyChanged -= OnViewFilterChanged;
        vm.CancelConversions(); // kill running ffmpeg jobs so none outlive the app
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        var focused = Keyboard.FocusedElement;
        bool inTextInput = focused is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem;
        var mods = Keyboard.Modifiers;

        if (mods == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            BtnNewDownload_Click(this, new RoutedEventArgs());
            return;
        }

        if (inTextInput || vm.IsPluginsView) return;

        if (mods == ModifierKeys.Control && e.Key == Key.V)
        {
            // NewDownloadDialog prefills itself from the clipboard on load.
            if (ClipboardHasHttpUrl())
            {
                e.Handled = true;
                BtnNewDownload_Click(this, new RoutedEventArgs());
            }
            return;
        }

        if (mods == ModifierKeys.Control && e.Key == Key.A)
        {
            e.Handled = true;
            DownloadList.SelectAll();
            DownloadList.Focus();
            return;
        }

        // Only while the list has focus (a stray Delete elsewhere must not remove downloads);
        // Space/Enter on a focused card button keep their normal meaning (press the button).
        if (mods != ModifierKeys.None || !DownloadList.IsKeyboardFocusWithin || focused is ButtonBase) return;

        var selected = vm.SelectedItems;
        if (selected.Count == 0) return;

        switch (e.Key)
        {
            case Key.Delete:
                e.Handled = true;
                _ = vm.DeleteItemsAsync(selected, alwaysConfirm: true);
                break;

            case Key.Space:
                e.Handled = true;
                if (selected.FirstOrDefault(d => d.CanPause) is { } pausable)
                    vm.PauseTaskCommand.Execute(pausable);
                else if (selected.FirstOrDefault(d => d.CanResume) is { } resumable)
                    vm.ResumeTaskCommand.Execute(resumable);
                break;

            case Key.Enter:
                e.Handled = true;
                OpenFinishedFile(DownloadList.SelectedItem as DownloadItemViewModel ?? selected[0]);
                break;
        }
    }

    private static bool ClipboardHasHttpUrl()
    {
        try
        {
            return Clipboard.ContainsText() && NewDownloadDialog.ParseUrls(Clipboard.GetText()).Count > 0;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false; // clipboard busy
        }
    }

    private void OpenFinishedFile(DownloadItemViewModel item)
    {
        if (item.Status != DownloadStatus.Completed || !File.Exists(item.DestinationPath)) return;
        if (item.ScanStatus == ScanStatus.Threat)
        {
            MessageBox.Show(this, "Windows Defender bu dosyada tehdit buldu; açılmadı.", "Dosya açılmadı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(item.DestinationPath) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Dosya açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
