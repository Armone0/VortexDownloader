using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VortexDownloader.Tests;

/// <summary>
/// Tiny loopback HTTP/1.1 server over raw sockets so tests can cut a body mid-stream, stall, or return 500.
/// Every response is "Connection: close". Range requests are honoured.
/// </summary>
internal sealed class RawHttpServer : IAsyncDisposable
{
    internal sealed record Request(string Method, long? From, long? To, int GetNumber);

    /// <summary>What to do with one GET. CutAfter = body bytes sent before acting; Stall waits for the gate; CloseAfterCut drops the connection.</summary>
    internal sealed record Plan(int Status = 0, long? CutAfter = null, Task? Stall = null, bool CloseAfterCut = false, Action? OnStalled = null);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _data;
    private readonly Func<Request, Plan> _plan;
    private readonly Task _acceptLoop;
    private int _gets;

    public RawHttpServer(byte[] data, Func<Request, Plan>? plan = null)
    {
        _data = data;
        _plan = plan ?? (_ => new Plan());
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file.bin";
    public int GetCount => Volatile.Read(ref _gets);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (requestLine == null) return;
                string method = requestLine.Split(' ')[0];
                long? from = null, to = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)))
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        string spec = line[(line.IndexOf('=') + 1)..].Trim();
                        string[] parts = spec.Split('-');
                        if (parts[0].Length > 0) from = long.Parse(parts[0]);
                        if (parts.Length > 1 && parts[1].Length > 0) to = long.Parse(parts[1]);
                    }
                }

                if (method == "HEAD")
                {
                    await WriteHeadAsync(stream, "200 OK", _data.Length, null).ConfigureAwait(false);
                    return;
                }

                int n = Interlocked.Increment(ref _gets);
                var plan = _plan(new Request(method, from, to, n));
                if (plan.Status != 0)
                {
                    await WriteHeadAsync(stream, $"{plan.Status} Error", 0, null).ConfigureAwait(false);
                    return;
                }

                long start = from ?? 0;
                long end = Math.Min(to ?? _data.Length - 1, _data.Length - 1);
                if (start > end)
                {
                    await WriteHeadAsync(stream, "416 Range Not Satisfiable", 0, null).ConfigureAwait(false);
                    return;
                }
                long length = end - start + 1;
                await WriteHeadAsync(stream, from.HasValue ? "206 Partial Content" : "200 OK", length,
                    from.HasValue ? $"Content-Range: bytes {start}-{end}/{_data.Length}\r\n" : null).ConfigureAwait(false);

                long cut = plan.CutAfter is { } c ? Math.Min(c, length) : length;
                await stream.WriteAsync(_data.AsMemory((int)start, (int)cut), _cts.Token).ConfigureAwait(false);
                if (cut == length) return;

                if (plan.Stall != null)
                {
                    plan.OnStalled?.Invoke();
                    // Like a real server, notice when the client hangs up instead of holding the socket for ever.
                    while (!plan.Stall.IsCompleted)
                    {
                        await Task.Delay(25, _cts.Token).ConfigureAwait(false);
                        if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) return;
                    }
                }
                if (plan.CloseAfterCut) return; // dropping the socket here ends the body early
                await stream.WriteAsync(_data.AsMemory((int)(start + cut), (int)(length - cut)), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* client went away (pause/cancel) or server is shutting down */ }
        }
    }

    private static async Task WriteHeadAsync(NetworkStream stream, string status, long length, string? extra)
    {
        string head = $"HTTP/1.1 {status}\r\nAccept-Ranges: bytes\r\nContent-Length: {length}\r\n{extra}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
