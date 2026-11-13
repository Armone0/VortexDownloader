using System.Windows;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;

namespace VortexDownloader.UI.Views;

/// <summary>"Başlangıç: Şimdi | Saat seç" and "Beklenen SHA-256" (kept apart from NewDownloadDialog.xaml.cs).</summary>
public partial class NewDownloadDialog
{
    /// <summary>Scheduled start in UTC, or null to start now.</summary>
    public DateTime? ResultStartAt { get; private set; }

    /// <summary>Normalized expected hash (lowercase hex), or null. Single downloads only.</summary>
    public string? ResultExpectedSha256 { get; private set; }

    private void StartAtRadio_Changed(object sender, RoutedEventArgs e)
    {
        bool on = StartAtRadio.IsChecked == true;
        StartTimePanel.IsEnabled = on;
        StartTimePanel.Opacity = on ? 1 : 0.5;
        if (!on || StartHourCombo.Items.Count > 0) return;

        StartHourCombo.ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToList();
        StartMinuteCombo.ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToList();
        var next = DateTime.Now.AddHours(1); // sensible default: next full hour
        StartHourCombo.SelectedIndex = next.Hour;
        StartMinuteCombo.SelectedIndex = 0;
    }

    /// <summary>Validates the schedule and hash fields; shows a message and returns false when invalid.</summary>
    private bool TryReadScheduleAndHash()
    {
        if (!FileHasher.TryNormalizeSha256(ExpectedHashTextBox.Text, out var hash))
        {
            MessageBox.Show(this, "Beklenen SHA-256 64 onaltılık karakter olmalı (0-9, a-f). Boşluklar yok sayılır.",
                "Geçersiz SHA-256", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExpectedHashTextBox.Focus();
            return false;
        }
        ResultExpectedSha256 = hash;

        ResultStartAt = null;
        if (StartAtRadio.IsChecked == true)
        {
            if (StartHourCombo.SelectedIndex < 0 || StartMinuteCombo.SelectedIndex < 0)
            {
                MessageBox.Show(this, "Lütfen başlangıç saatini seçin.", "Başlangıç", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            ResultStartAt = DownloadSchedule.NextOccurrence(DateTime.Now, StartHourCombo.SelectedIndex, StartMinuteCombo.SelectedIndex)
                .ToUniversalTime();
        }
        return true;
    }
}
