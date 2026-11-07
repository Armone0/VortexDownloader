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
    private const int BufferSize = 81920; // 80 KB chunk buffer

    public static HttpClient CreateOptimizedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 64,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None // Essential for byte-range integrity
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VortexDownloader/1.0 (Clean-Room Modern Engine)");
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
            BufferSize,
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

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

                chunk.CurrentPos += bytesRead;
                onBytesReceived?.Invoke(bytesRead);

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
