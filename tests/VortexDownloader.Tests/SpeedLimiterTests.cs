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

    [Fact]
    public async Task ConsumeAsync_AbsorbsSub10msDeficit_WithoutCausingTimerQuantizationDelay()
    {
        // Limit: 10 MB/s. 64 KB capacity.
        var limiter = new SpeedLimiter(10_000_000);

        // First consume 64 KB to drain bucket to 0
        await limiter.ConsumeAsync(64 * 1024);

        // Now consume 10 KB (missing 10 KB -> 10,000 / 10,000,000 = 1 ms delay)
        // Since 1ms < 10ms threshold, it should complete immediately without sleeping 15ms
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.ConsumeAsync(10_000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 8, $"Expected < 8ms, but was {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ConsumeAsync_DelaysAccurately_WhenDeficitExceedsThreshold()
    {
        // Limit: 1 MB/s (1,000,000 B/s)
        var limiter = new SpeedLimiter(1_000_000);

        // Drain bucket
        await limiter.ConsumeAsync((int)limiter.BucketCapacity);

        // Request 50 KB (50,000 / 1,000,000 = 50ms delay)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.ConsumeAsync(50_000);
        sw.Stop();

        // Should delay around 50ms (allow standard timer slack: >= 35ms)
        Assert.True(sw.ElapsedMilliseconds >= 35, $"Expected >= 35ms, but was {sw.ElapsedMilliseconds}ms");
    }
}
