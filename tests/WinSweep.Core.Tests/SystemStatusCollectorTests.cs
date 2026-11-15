using System.Net.NetworkInformation;
using WinSweep.Core.Platform;
using WinSweep.Core.Status;
using Xunit;

namespace WinSweep.Core.Tests;

public class SystemStatusCollectorTests
{
    [Fact]
    public void SnapshotSupportIsTheInverseOfAStoreBuild()
    {
        // One predicate is shared by the Settings toggle and the sweep runner, so they
        // cannot disagree about whether a volume snapshot is possible.
        Assert.Equal(!AppEnvironment.IsStoreBuild, AppEnvironment.SupportsVolumeSnapshots);
    }

    [Fact]
    public void LoopbackIsNotCountedAsAnActiveNetwork()
    {
        Assert.False(SystemStatusCollector.CountsAsActiveNetwork(NetworkInterfaceType.Loopback));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet)]
    [InlineData(NetworkInterfaceType.Wireless80211)]
    [InlineData(NetworkInterfaceType.Tunnel)]
    public void RealAdaptersAreCountedAsActive(NetworkInterfaceType type)
    {
        Assert.True(SystemStatusCollector.CountsAsActiveNetwork(type));
    }
}
