using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class ShutdownManagerTests
{
    [Fact]
    public void AbortShutdown_ExecutesSafelyWithoutExceptions()
    {
        // Safe to call even if no shutdown is scheduled
        var exception = Record.Exception(() => ShutdownManager.AbortShutdown());
        Assert.Null(exception);
    }
}
