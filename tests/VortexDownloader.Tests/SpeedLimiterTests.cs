using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class SpeedLimiterTests
{
    [Fact]
    public void CalculateWaitTimeSeconds_ReturnsZero_WhenTokensAreSufficient()
    {
        double currentTokens = 5000;
        int requestedBytes = 2048;
        long limitBytesPerSec = 10_000;

        double wait = SpeedLimiter.CalculateWaitTimeSeconds(currentTokens, requestedBytes, limitBytesPerSec);

        Assert.Equal(0.0, wait);
    }

    [Fact]
    public void CalculateWaitTimeSeconds_ReturnsExactRequiredDelay_WhenTokensDeficient()
    {
        double currentTokens = 1000;
        int requestedBytes = 3000; // missing 2000 bytes
        long limitBytesPerSec = 2000; // 2000 bytes/sec -> exactly 1.0 second wait

        double wait = SpeedLimiter.CalculateWaitTimeSeconds(currentTokens, requestedBytes, limitBytesPerSec);

        Assert.Equal(1.0, wait);
    }

    [Fact]
    public void CalculateWaitTimeSeconds_ReturnsZero_WhenLimitIsZeroOrNegative()
    {
        double currentTokens = 0;
        int requestedBytes = 5000;
        long limitBytesPerSec = 0; // Unlimited

        double wait = SpeedLimiter.CalculateWaitTimeSeconds(currentTokens, requestedBytes, limitBytesPerSec);

        Assert.Equal(0.0, wait);
    }

    [Fact]
    public async Task ConsumeAsync_CompletesImmediately_WhenUnlimited()
    {
        var limiter = new SpeedLimiter(0); // Unlimited

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await limiter.ConsumeAsync(1_000_000);
        watch.Stop();

        // Unlimited must return essentially immediately (< 50ms)
        Assert.True(watch.ElapsedMilliseconds < 50);
    }

    [Fact]
    public void BucketCapacity_EnforcesOneSecondLimit_WithMinimumFloor()
    {
        var limiterSmall = new SpeedLimiter(10_000); // 10 KB/s
        Assert.Equal(64 * 1024, limiterSmall.BucketCapacity); // 64 KB floor

        var limiterLarge = new SpeedLimiter(5_000_000); // 5 MB/s
        Assert.Equal(5_000_000, limiterLarge.BucketCapacity);
    }
}
