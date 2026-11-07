using System.Diagnostics;

namespace VortexDownloader.Core.Common;

public class SpeedLimiter
{
    private readonly object _syncLock = new();
    private readonly TimeProvider _timeProvider;
    private long _bytesPerSecondLimit;
    private double _availableTokens;
    private double _bucketCapacity;
    private long _lastTimestamp;

    public SpeedLimiter(long bytesPerSecondLimit = 0, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        SetLimit(bytesPerSecondLimit);
        _lastTimestamp = _timeProvider.GetTimestamp();
    }

    public long BytesPerSecondLimit
    {
        get
        {
            lock (_syncLock)
            {
                return _bytesPerSecondLimit;
            }
        }
    }

    public double AvailableTokens
    {
        get
        {
            lock (_syncLock)
            {
                RefillTokens();
                return _availableTokens;
            }
        }
    }

    public double BucketCapacity
    {
        get
        {
            lock (_syncLock)
            {
                return _bucketCapacity;
            }
        }
    }

    public void SetLimit(long bytesPerSecondLimit)
    {
        lock (_syncLock)
        {
            _bytesPerSecondLimit = Math.Max(0, bytesPerSecondLimit);
            if (_bytesPerSecondLimit > 0)
            {
                // Bucket capacity set to 1 second worth of bandwidth, minimum 64 KB
                _bucketCapacity = Math.Max(_bytesPerSecondLimit, 64 * 1024);
                _availableTokens = Math.Min(_availableTokens > 0 ? _availableTokens : _bucketCapacity, _bucketCapacity);
            }
            else
            {
                _bucketCapacity = 0;
                _availableTokens = 0;
            }
            _lastTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private void RefillTokens()
    {
        if (_bytesPerSecondLimit <= 0)
        {
            return;
        }

        long now = _timeProvider.GetTimestamp();
        long diff = now - _lastTimestamp;
        if (diff <= 0)
        {
            return;
        }

        double elapsedSeconds = (double)diff / _timeProvider.TimestampFrequency;
        _lastTimestamp = now;

        double addedTokens = elapsedSeconds * _bytesPerSecondLimit;
        _availableTokens = Math.Min(_bucketCapacity, _availableTokens + addedTokens);
    }

    public async ValueTask ConsumeAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount <= 0) return;

        double delaySeconds = 0;

        lock (_syncLock)
        {
            if (_bytesPerSecondLimit <= 0)
            {
                return; // Unlimited speed
            }

            RefillTokens();

            if (_availableTokens >= byteCount)
            {
                _availableTokens -= byteCount;
                return;
            }

            // Need more tokens: compute waiting time
            double missingTokens = byteCount - _availableTokens;
            delaySeconds = missingTokens / _bytesPerSecondLimit;

            // Debit tokens so following calls also account for this
            _availableTokens -= byteCount;
        }

        if (delaySeconds > 0)
        {
            TimeSpan delay = TimeSpan.FromSeconds(delaySeconds);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public static double CalculateWaitTimeSeconds(double currentTokens, int requestedBytes, long limitBytesPerSec)
    {
        if (limitBytesPerSec <= 0) return 0.0;
        if (currentTokens >= requestedBytes) return 0.0;
        double missing = requestedBytes - currentTokens;
        return missing / limitBytesPerSec;
    }
}
