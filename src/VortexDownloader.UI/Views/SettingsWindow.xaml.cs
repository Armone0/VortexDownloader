using System.IO;
using System.Windows;
using System.Windows.Controls;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Network;
using VortexDownloader.UI.Services;
using VortexDownloader.UI.ViewModels;

namespace VortexDownloader.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly string _initialTheme;
    private readonly string _initialLanguage;
    private bool _isInitialized;

    public SettingsWindow()
    {
        InitializeComponent();

        _initialTheme = AppSettings.Current.Theme;
        _initialLanguage = AppSettings.Current.Language;

        Loaded += SettingsWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;

        // Populate Default Folder
        TxtDefaultFolder.Text = settings.DefaultDownloadDirectory;

        // Populate Max Concurrent
        SelectComboBoxItemByTag(CmbMaxConcurrent, settings.MaxConcurrentDownloads.ToString());

        // Populate Default Chunks
        SelectComboBoxItemByTag(CmbDefaultChunks, settings.DefaultChunkCount.ToString());

        // Populate Auto-Start
        ChkAutoStartBrowserDownloads.IsChecked = settings.AutoStartBrowserDownloads;

        // Populate Close to Tray
        ChkCloseToTray.IsChecked = settings.CloseToTray;

        // Populate Shutdown
        ChkShutdownWhenDone.IsChecked = settings.ShutdownWhenCompleted;
        ChkScanWithDefender.IsChecked = settings.ScanWithDefender;

        // Populate Game Mode (unknown saved value falls back to 512)
        ChkGameMode.IsChecked = settings.GameModeEnabled;
        SelectComboBoxItemByTag(CmbGameModeLimit, "512");
        SelectComboBoxItemByTag(CmbGameModeLimit, settings.GameModeLimitKBps.ToString());

        ChkAutoFolderByType.IsChecked = settings.AutoFolderByType;
        ChkWatchClipboard.IsChecked = settings.WatchClipboardLinks;
        ChkCheckUpdates.IsChecked = settings.CheckUpdatesOnStartup;

        SelectComboBoxItemByTag(CmbProxyMode, settings.ProxyMode);
        TxtProxyHost.Text = settings.ProxyHost;
        TxtProxyPort.Text = settings.ProxyPort.ToString();
        TxtProxyUser.Text = settings.ProxyUser;
        TxtProxyPassword.Password = SecretProtector.Unprotect(settings.ProxyPasswordProtected) ?? "";
        TxtShortcuts.Text = MainWindow.ShortcutsHelp;
        if (Owner?.DataContext is MainViewModel vm)
        {
            TxtMonthStats.Text = DownloadStats.Format(DownloadStats.ForMonth(vm.Engine.Tasks, DateTime.Now));
        }

        // Populate Theme
        SelectComboBoxItemByTag(CmbTheme, settings.Theme);

        // Populate Language
        SelectComboBoxItemByTag(CmbLanguage, settings.Language);

        _isInitialized = true;
    }

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tagValue)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = cbi;
                return;
            }
        }
        if (comboBox.Items.Count > 0 && comboBox.SelectedIndex == -1)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        if (CmbTheme.SelectedItem is ComboBoxItem item && item.Tag is string themeTag)
        {
            ThemeManager.SetTheme(themeTag);
        }
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string langTag)
        {
            LocalizationManager.SetLanguage(langTag);
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationManager.GetString("Str_DefaultFolder", "Varsayılan Klasör"),
                InitialDirectory = Directory.Exists(TxtDefaultFolder.Text) 
                    ? TxtDefaultFolder.Text 
                    : AppSettings.GetDefaultDownloadPath()
            };

            if (dialog.ShowDialog(this) == true)
            {
                TxtDefaultFolder.Text = dialog.FolderName;
            }
        }
        catch
        {
            // Fallback gracefully if folder dialog fails
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateResult.Text = LocalizationManager.GetString("Str_UpdateChecking", "Kontrol ediliyor…");
        try
        {
            string? newer = await UpdateChecker.FindNewerVersionAsync();
            if (newer == null)
            {
                TxtUpdateResult.Text = string.Format(LocalizationManager.GetString("Str_UpToDate", "Güncelsiniz (v{0})."), AppInfo.Version);
            }
            else
            {
                TxtUpdateResult.Text = string.Format(LocalizationManager.GetString("Str_UpdateFound", "v{0} çıktı."), newer);
                (Owner as MainWindow)?.ShowUpdateBanner(newer, respectSkip: false);
            }
        }
        catch (Exception ex)
        {
            TxtUpdateResult.Text = string.Format(LocalizationManager.GetString("Str_UpdateCheckFailed", "Kontrol edilemedi: {0}"), ex.Message);
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private void CmbProxyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyManualPanel == null) return; // fires during InitializeComponent
        ProxyManualPanel.Visibility = SelectedProxyMode() == "Manual" ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SelectedProxyMode() => (CmbProxyMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // Validated before the live settings object is touched.
        string proxyMode = SelectedProxyMode();
        int.TryParse(TxtProxyPort.Text.Trim(), out int proxyPort);
        if (proxyMode == "Manual")
        {
            try
            {
                ProxyFactory.Create(ProxyMode.Manual, TxtProxyHost.Text, proxyPort);
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                MessageBox.Show(this, LocalizationManager.GetString("Str_ProxyInvalid", "Proxy sunucusu ya da portu geçersiz."),
                    LocalizationManager.GetString("Str_Proxy", "Proxy"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var settings = AppSettings.Current;
        settings.ProxyMode = proxyMode;
        settings.ProxyHost = TxtProxyHost.Text.Trim();
        if (proxyPort is > 0 and <= 65535) settings.ProxyPort = proxyPort;
        settings.ProxyUser = TxtProxyUser.Text.Trim();
        settings.ProxyPasswordProtected = SecretProtector.Protect(TxtProxyPassword.Password);

        settings.Theme = ThemeManager.CurrentTheme;
        settings.Language = LocalizationManager.CurrentLanguage;

        if (!string.IsNullOrWhiteSpace(TxtDefaultFolder.Text))
        {
            settings.DefaultDownloadDirectory = TxtDefaultFolder.Text;
        }

        if (CmbMaxConcurrent.SelectedItem is ComboBoxItem maxItem && int.TryParse(maxItem.Tag?.ToString(), out int maxVal))
        {
            settings.MaxConcurrentDownloads = maxVal;
        }

        if (CmbDefaultChunks.SelectedItem is ComboBoxItem chunkItem && int.TryParse(chunkItem.Tag?.ToString(), out int chunkVal))
        {
            settings.DefaultChunkCount = chunkVal;
        }

        settings.AutoStartBrowserDownloads = ChkAutoStartBrowserDownloads.IsChecked == true;
        settings.CloseToTray = ChkCloseToTray.IsChecked == true;
        settings.ShutdownWhenCompleted = ChkShutdownWhenDone.IsChecked == true;
        settings.ScanWithDefender = ChkScanWithDefender.IsChecked == true;
        settings.GameModeEnabled = ChkGameMode.IsChecked == true;
        settings.AutoFolderByType = ChkAutoFolderByType.IsChecked == true;
        settings.WatchClipboardLinks = ChkWatchClipboard.IsChecked == true;
        settings.CheckUpdatesOnStartup = ChkCheckUpdates.IsChecked == true;
        if (CmbGameModeLimit.SelectedItem is ComboBoxItem gameItem && int.TryParse(gameItem.Tag?.ToString(), out int gameVal))
        {
            settings.GameModeLimitKBps = gameVal;
        }
        settings.Save();

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // Revert real-time previews if cancelled
        if (ThemeManager.CurrentTheme != _initialTheme)
        {
            ThemeManager.SetTheme(_initialTheme);
        }

        if (LocalizationManager.CurrentLanguage != _initialLanguage)
        {
            LocalizationManager.SetLanguage(_initialLanguage);
        }

        DialogResult = false;
        Close();
    }
}
