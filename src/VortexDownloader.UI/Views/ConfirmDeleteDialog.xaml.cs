using System.IO;
using System.Windows;
using System.Windows.Input;

namespace VortexDownloader.UI.Views;

public partial class ConfirmDeleteDialog : Window
{
    private ConfirmDeleteDialog(string fileName, string filePath)
    {
        InitializeComponent();
        FileNameText.Text = fileName;
        string folder = Path.GetDirectoryName(filePath) ?? filePath;
        long size = 0;
        try { size = new FileInfo(filePath).Length; } catch { }
        FileInfoText.Text = size > 0 ? $"{FormatSize(size)}  ·  {folder}" : folder;
        FileInfoText.ToolTip = filePath;
    }

    /// <summary>null = cancelled, false = remove from list only, true = also delete the file.</summary>
    public static bool? Ask(Window? owner, string fileName, string filePath)
    {
        var dialog = new ConfirmDeleteDialog(fileName, filePath);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.DeleteFileCheck.IsChecked == true : null;
    }

    /// <summary>Multi-select delete: one dialog for all items. The checkbox deletes only the finished files in <paramref name="filePaths"/>.</summary>
    public static bool? AskMany(Window? owner, int itemCount, IReadOnlyList<string> filePaths)
    {
        var dialog = new ConfirmDeleteDialog(filePaths[0], filePaths[0]);
        dialog.FileNameText.Text = $"{itemCount} indirme ({filePaths.Count} dosya diskte)";
        dialog.SubtitleText.Text = "Seçili indirmeler listeden kaldırılacak.";
        dialog.DeleteFileLabel.Text = "Bitmiş dosyaları diskten de kalıcı olarak sil";
        long total = filePaths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
        var folders = filePaths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string where = folders.Count == 1 ? folders[0] ?? "" : $"{folders.Count} klasör";
        dialog.FileInfoText.Text = total > 0 ? $"{FormatSize(total)}  ·  {where}" : where;
        dialog.FileInfoText.ToolTip = string.Join("\n", filePaths.Take(20));
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.DeleteFileCheck.IsChecked == true : null;
    }

    private void DeleteFileCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool deleteFile = DeleteFileCheck.IsChecked == true;
        ConfirmButton.Content = deleteFile ? "Kaldır ve dosyayı sil" : "Listeden kaldır";
        string bg = deleteFile ? "StatusErrorBrush" : "ButtonPrimaryBgBrush";
        ConfirmButton.SetResourceReference(BackgroundProperty, bg);
        ConfirmButton.SetResourceReference(BorderBrushProperty, bg);
        ConfirmButton.SetResourceReference(ForegroundProperty, deleteFile ? "WindowBackgroundBrush" : "ButtonPrimaryFgBrush");
        WarningText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, deleteFile ? "StatusErrorBrush" : "TextMutedBrush");
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
