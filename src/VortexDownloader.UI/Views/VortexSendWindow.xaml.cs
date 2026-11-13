using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using VortexDownloader.Core.Network;

namespace VortexDownloader.UI.Views;

/// <summary>
/// Shows a QR code for <see cref="VortexSendServer"/>. The server lives exactly as long as this window,
/// capped at <see cref="Lifetime"/>. Received links go to <c>onUrl</c>, which must still ask the user to confirm.
/// </summary>
public partial class VortexSendWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Action<string> _onUrl;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _deadline = DateTime.UtcNow + Lifetime;
    private VortexSendServer? _server;

    public VortexSendWindow(Action<string> onUrl)
    {
        InitializeComponent();
        _onUrl = onUrl;
        _timer.Tick += Timer_Tick;
        Closed += (_, _) => StopServer();
        StartServer();
    }

    private void StartServer()
    {
        var address = VortexSendServer.FindLanAddress();
        if (address == null)
        {
            ShowError("Yerel ağ (Wi-Fi/Ethernet) bağlantısı bulunamadı. Bilgisayarın bir ağa bağlı olduğundan emin ol.");
            return;
        }

        var server = new VortexSendServer(address, OnUrlReceived);
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            server.Dispose();
            ShowError($"Sunucu başlatılamadı: {ex.Message}");
            return;
        }

        _server = server;
        server.LockedOut += () => Dispatcher.BeginInvoke(() =>
        {
            StopServer();
            ShowError("Çok fazla hatalı deneme. Güvenlik için kapatıldı; pencereyi yeniden aç.");
        });

        UrlText.Text = server.Url;
        QrImage.Source = RenderQr(server.Url);
        _timer.Start();
        UpdateCountdown();
    }

    private void OnUrlReceived(string url)
    {
        // Worker thread. The caller marshals the confirmation prompt to the UI thread itself.
        _onUrl(url);
        Dispatcher.BeginInvoke(() => StatusText.Text = "Bağlantı alındı, onay bekliyor.");
    }

    private static BitmapImage RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        using var stream = new MemoryStream(png.GetGraphic(8));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // decode now so the stream can be disposed
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _deadline) Close();
        else UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        var left = _deadline - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        CountdownText.Text = $"Kalan süre: {left:mm\\:ss}";
    }

    private void ShowError(string message)
    {
        QrImage.Source = null;
        UrlText.Text = "";
        CountdownText.Text = "";
        StatusText.Text = message;
    }

    private void StopServer()
    {
        _timer.Stop();
        _server?.Dispose();
        _server = null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
