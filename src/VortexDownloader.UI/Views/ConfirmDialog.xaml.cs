using System.Windows;
using System.Windows.Input;

namespace VortexDownloader.UI.Views;

/// <summary>Themed yes/no dialog. The confirm button is deliberately not the Enter default, so a stray keypress cannot approve.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog() => InitializeComponent();

    public static bool Ask(Window? owner, string title, string message, string? detail, string confirmText, string glyph = "")
    {
        var dialog = new ConfirmDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.DetailText.Text = detail ?? "";
        dialog.DetailCard.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        dialog.ConfirmButton.Content = confirmText;
        dialog.IconGlyph.Text = glyph;
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else
        {
            // App may be in the tray: show on top so the request is not missed
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
