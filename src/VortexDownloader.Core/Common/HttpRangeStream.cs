using System.Net;
using System.Net.Http.Headers;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Read-only, seekable view of a remote file backed by HTTP Range requests, so archive readers can jump to headers
/// without downloading the whole file. Random reads fetch small windows; sequential reads grow the window (up to 4 MB)
/// to keep the request count low while streaming an entry. Synchronous reads block: call from a worker thread.
/// </summary>
public sealed class HttpRangeStream : Stream
{
    private const int MinWindow = 64 * 1024;
    private const int MaxWindow = 4 * 1024 * 1024;
    private const int MaxCachedWindows = 4;

    private readonly HttpClient _client;
    private readonly string _url;
    private readonly string? _cookies;
    private readonly string? _referrer;
    private readonly CancellationToken _cancellationToken;
    private readonly LinkedList<(long Start, byte[] Data)> _windows = new();
    private long _position;
    private long _lastFetchEnd = -1;
    private int _nextWindow = MinWindow;

    /// <summary>Bytes received from the server so far (for progress / cost reporting).</summary>
    public long BytesFetched { get; private set; }

    public HttpRangeStream(HttpClient client, string url, long length, string? cookies = null, string? referrer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _client = client;
        _url = url;
        Length = length;
        _cookies = cookies;
        _referrer = referrer;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Total size via a one-byte Range probe; throws when the server does not honour Range.</summary>
    public static async Task<long> GetRangeLengthAsync(HttpClient client, string url, string? cookies, string? referrer,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(url, 0, 0, cookies, referrer);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
            throw new NotSupportedException("Sunucu HTTP Range isteklerini desteklemiyor; arşivin içi önceden okunamaz.");
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"Sunucu isteği reddetti: HTTP {(int)response.StatusCode}. Bağlantının süresi dolmuş ya da giriş gerekiyor olabilir.");
        return response.Content.Headers.ContentRange?.Length
            ?? throw new InvalidDataException("Sunucu dosya boyutunu bildirmedi (Content-Range).");
    }

    private static HttpRequestMessage CreateRequest(string url, long from, long to, string? cookies, string? referrer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        // Compressed transfer would break byte offsets.
        request.Headers.AcceptEncoding.Clear();
        if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (!string.IsNullOrEmpty(referrer)) request.Headers.TryAddWithoutValidation("Referer", referrer);
        return request;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => Length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var (start, data) = GetWindow(_position);
            int index = (int)(_position - start);
            int n = Math.Min(buffer.Length, data.Length - index);
            data.AsSpan(index, n).CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
        }
        return total;
    }

    private (long Start, byte[] Data) GetWindow(long position)
    {
        for (var node = _windows.First; node != null; node = node.Next)
        {
            if (position >= node.Value.Start && position < node.Value.Start + node.Value.Data.Length)
            {
                _windows.Remove(node);
                _windows.AddFirst(node);
                return node.Value;
            }
        }

        // Continuing right where the last fetch ended = streaming: double the window; otherwise a header jump: start small.
        _nextWindow = position == _lastFetchEnd ? Math.Min(_nextWindow * 2, MaxWindow) : MinWindow;
        long end = Math.Min(Length, position + _nextWindow) - 1;
        byte[] data = Fetch(position, end);
        _lastFetchEnd = position + data.Length;

        _windows.AddFirst((position, data));
        while (_windows.Count > MaxCachedWindows) _windows.RemoveLast();
        return (position, data);
    }

    private byte[] Fetch(long from, long to)
    {
        using var request = CreateRequest(_url, from, to, _cookies, _referrer);
        using var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"Range isteği başarısız: HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentRange?.From is long gotFrom && gotFrom != from)
            throw new InvalidDataException("Sunucu istenen aralık yerine başka bir aralık gönderdi.");

        int expected = checked((int)(to - from + 1));
        byte[] data = new byte[expected];
        using var body = response.Content.ReadAsStream(_cancellationToken);
        int read = 0, n;
        while (read < expected && (n = body.Read(data, read, expected - read)) > 0) read += n;
        if (read == 0) throw new EndOfStreamException("Sunucu boş yanıt döndü.");
        BytesFetched += read;
        return read == expected ? data : data[..read];
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
