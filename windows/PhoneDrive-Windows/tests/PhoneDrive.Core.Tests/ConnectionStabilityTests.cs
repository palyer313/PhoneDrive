using PhoneDrive.Core.Discovery;

namespace PhoneDrive.Core.Tests;

public sealed class ConnectionStabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TransportMustFailRepeatedlyForTwentySecondsBeforeOffline()
    {
        var p = new ConnectionStabilityPolicy();
        Assert.False(p.ConfirmOffline("phone", false, Start));
        Assert.False(p.ConfirmOffline("phone", false, Start.AddSeconds(10)));
        Assert.False(p.ConfirmOffline("phone", false, Start.AddSeconds(19)));
        Assert.True(p.ConfirmOffline("phone", false, Start.AddSeconds(20)));
    }

    [Fact]
    public void HealthyTransportResetsLossEvenWhenDiscoveryIsMissing()
    {
        var p = new ConnectionStabilityPolicy();
        Assert.False(p.ConfirmOffline("phone", false, Start));
        Assert.False(p.ConfirmOffline("phone", true, Start.AddSeconds(15)));
        Assert.False(p.ConfirmOffline("phone", false, Start.AddSeconds(21)));
        Assert.False(p.ConfirmOffline("phone", false, Start.AddSeconds(30)));
    }

    [Fact]
    public void MountFailureCannotProduceOverlappingOrImmediateRetries()
    {
        var p = new ConnectionStabilityPolicy();
        Assert.True(p.TryBeginMount("phone", Start));
        Assert.False(p.TryBeginMount("phone", Start));
        p.EndMount("phone", false, Start);
        Assert.False(p.TryBeginMount("phone", Start.AddSeconds(4)));
        Assert.True(p.TryBeginMount("phone", Start.AddSeconds(5)));
        p.EndMount("phone", false, Start.AddSeconds(5));
        Assert.False(p.TryBeginMount("phone", Start.AddSeconds(14)));
        Assert.True(p.TryBeginMount("phone", Start.AddSeconds(15)));
        p.EndMount("phone", true, Start.AddSeconds(15));
        Assert.True(p.TryBeginMount("phone", Start.AddSeconds(16)));
    }

    [Fact]
    public void NotificationsCoalesceSameDeviceAndBoundOverallRate()
    {
        var g = new NotificationGate();
        Assert.True(g.TryPublish("mount:phone", Start));
        Assert.False(g.TryPublish("mount:phone", Start.AddSeconds(40)));
        Assert.False(g.TryPublish("offline:phone", Start.AddSeconds(1)));
        Assert.True(g.TryPublish("offline:phone", Start.AddSeconds(11)));
        Assert.True(g.TryPublish("mount:phone", Start.AddMinutes(5)));
    }

    [Fact]
    public void IdentityFailureMayNotifyImmediatelyButNeverRepeatsWithinCooldown()
    {
        var g = new NotificationGate();
        Assert.True(g.TryPublish("mount:phone", Start));
        Assert.True(g.TryPublish("identity:phone", Start.AddSeconds(1), true));
        Assert.False(g.TryPublish("identity:phone", Start.AddSeconds(2), true));
    }
}
