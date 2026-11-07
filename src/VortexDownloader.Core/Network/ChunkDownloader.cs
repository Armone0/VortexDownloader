using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Network;

public class ChunkDownloader
{
    private const int BufferSize = 131072; // 128 KB buffer, perfectly aligned with ArrayPool 2^17 bucket

    public static HttpClient CreateOptimizedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 64,
            EnableMultipleHttp2Connections = true,
            InitialHttp2StreamWindowSize = 16 * 1024 * 1024, // 16 MB flow control window prevents Gigabit BDP bottleneck
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None // Essential for byte-range integrity
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        return client;
    }

    public static async Task DownloadChunkAsync(
        HttpClient httpClient,
        string url,
        DownloadChunk chunk,
        SpeedLimiter speedLimiter,
        Action<int>? onBytesReceived = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(speedLimiter);

        const int maxRetries = 3;
        int attempt = 0;

        while (attempt < maxRetries)
        {
            try
            {
                await DownloadChunkInternalAsync(httpClient, url, chunk, speedLimiter, onBytesReceived, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < maxRetries - 1 && !cancellationToken.IsCancellationRequested)
            {
                attempt++;
                int delayMs = (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }) 
                    ? attempt * 2000 
                    : attempt * 1000;
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DownloadChunkInternalAsync(
        HttpClient httpClient,
        string url,
        DownloadChunk chunk,
        SpeedLimiter speedLimiter,
        Action<int>? onBytesReceived,
        CancellationToken cancellationToken)
    {
        if (chunk.IsCompleted)
        {
            return;
        }

        // If chunk is already completed by position check
        if (chunk.EndPos >= 0 && chunk.CurrentPos > chunk.EndPos)
        {
            chunk.IsCompleted = true;
            return;
        }

        string? dir = Path.GetDirectoryName(chunk.TempFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        long downloadedBytesSoFar = chunk.DownloadedBytes;

        await using var fileStream = new FileStream(
            chunk.TempFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096, // 4 KB stream buffer: 128 KB writes bypass managed buffer directly to OS
            useAsync: true);

        // Position file pointer to current downloaded offset
        if (fileStream.Length > downloadedBytesSoFar)
        {
            fileStream.SetLength(downloadedBytesSoFar);
        }
        fileStream.Seek(downloadedBytesSoFar, SeekOrigin.Begin);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        long startByte = chunk.CurrentPos;
        if (chunk.EndPos >= 0)
        {
            if (startByte > chunk.EndPos)
            {
                chunk.IsCompleted = true;
                return;
            }
            request.Headers.Range = new RangeHeaderValue(startByte, chunk.EndPos);
        }
        else if (startByte > 0)
        {
            request.Headers.Range = new RangeHeaderValue(startByte, null);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            chunk.IsCompleted = true;
            return;
        }

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int maxToRead = buffer.Length;
                if (chunk.EndPos >= 0)
                {
                    long remaining = chunk.EndPos - chunk.CurrentPos + 1;
                    if (remaining <= 0)
                    {
                        chunk.IsCompleted = true;
                        break;
                    }
                    maxToRead = (int)Math.Min(buffer.Length, remaining);
                }

                int bytesRead = await contentStream.ReadAsync(
                    buffer.AsMemory(0, maxToRead),
                    cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    // Stream ended
                    if (chunk.EndPos < 0 || chunk.CurrentPos >= chunk.EndPos)
                    {
                        chunk.IsCompleted = true;
                    }
                    break;
                }

                await speedLimiter.ConsumeAsync(bytesRead, cancellationToken).ConfigureAwait(false);

                int maxBytesToWrite = bytesRead;
                if (chunk.EndPos >= 0)
                {
                    long allowedToWrite = chunk.EndPos - chunk.CurrentPos + 1;
                    if (allowedToWrite <= 0)
                    {
                        chunk.IsCompleted = true;
                        break;
                    }
                    maxBytesToWrite = (int)Math.Min(bytesRead, allowedToWrite);
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, maxBytesToWrite), cancellationToken).ConfigureAwait(false);

                chunk.CurrentPos += maxBytesToWrite;
                onBytesReceived?.Invoke(maxBytesToWrite);

                if (chunk.EndPos >= 0 && chunk.CurrentPos > chunk.EndPos)
                {
                    chunk.IsCompleted = true;
                    break;
                }
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
