using System.IO;
using System.Windows;
using System.Windows.Controls;
using VortexDownloader.UI.Services;

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

        // Populate Close to Tray
        ChkCloseToTray.IsChecked = settings.CloseToTray;

        // Populate Shutdown
        ChkShutdownWhenDone.IsChecked = settings.ShutdownWhenCompleted;

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

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;

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

        settings.CloseToTray = ChkCloseToTray.IsChecked == true;
        settings.ShutdownWhenCompleted = ChkShutdownWhenDone.IsChecked == true;
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
