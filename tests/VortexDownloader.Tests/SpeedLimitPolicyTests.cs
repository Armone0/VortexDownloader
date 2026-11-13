using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class SpeedLimitPolicyTests
{
    [Theory]
    [InlineData(0, false)] // unknown
    [InlineData(1, false)] // QUNS_NOT_PRESENT
    [InlineData(2, true)]  // QUNS_BUSY
    [InlineData(3, true)]  // QUNS_RUNNING_D3D_FULL_SCREEN
    [InlineData(4, true)]  // QUNS_PRESENTATION_MODE
    [InlineData(5, false)] // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(6, false)] // QUNS_QUIET_TIME
    [InlineData(7, false)] // QUNS_APP
    public void IsFullscreenState_MapsShellStates(int state, bool expected)
    {
        Assert.Equal(expected, SpeedLimitPolicy.IsFullscreenState(state));
    }

    [Theory]
    [InlineData(0, 0, 0)]                 // both unlimited
    [InlineData(0, 524288, 524288)]       // user unlimited -> game limit
    [InlineData(1048576, 0, 1048576)]     // game unlimited -> user limit
    [InlineData(1048576, 524288, 524288)] // game lower
    [InlineData(262144, 524288, 262144)]  // user already lower -> kept
    [InlineData(-1, 524288, 524288)]      // negative treated as unlimited
    public void CombineLimits_PicksStricterLimit(long user, long game, long expected)
    {
        Assert.Equal(expected, SpeedLimitPolicy.CombineLimits(user, game));
    }
}
