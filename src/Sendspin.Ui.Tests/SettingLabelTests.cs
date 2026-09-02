using System.Reflection;
using Sendspin.Core.Configuration;
using Sendspin.Player.Converters;
using Sendspin.SDK.Client;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the friendly names the settings pickers show for their enums, member by member, so a
/// new member fails here rather than showing its raw name in the card.
/// </summary>
public sealed class SettingLabelTests
{
    [Theory]
    [InlineData(ConnectionMode.AdvertiseOnly, "Advertise to servers, and let a server connect")]
    [InlineData(ConnectionMode.DiscoverOnly, "Discover servers, and connect from here")]
    public void ConnectionMode_HasAFriendlyName(ConnectionMode mode, string expected) =>
        Assert.Equal(expected, ConnectionModeLabel.For(mode));

    /// <remarks>
    /// Every member that is not obsolete has a label. <c>Auto</c> is obsolete on this SDK line
    /// and gone on the next; the picker never offers it, so it is the one member allowed to fall
    /// through to its raw name.
    /// </remarks>
    [Fact]
    public void ConnectionMode_EveryOfferedMemberIsNamed()
    {
        foreach (var mode in Enum.GetValues<ConnectionMode>())
        {
            if (IsObsolete(mode))
            {
                continue;
            }

            Assert.NotEqual(mode.ToString(), ConnectionModeLabel.For(mode));
        }
    }

    [Theory]
    [InlineData(AutoConnectPolicy.Never, "Never")]
    [InlineData(AutoConnectPolicy.JustOnce, "Just once")]
    [InlineData(AutoConnectPolicy.Always, "Always")]
    public void AutoConnectPolicy_HasAFriendlyName(AutoConnectPolicy policy, string expected) =>
        Assert.Equal(expected, AutoConnectPolicyLabel.For(policy));

    [Fact]
    public void AutoConnectPolicy_EveryMemberIsNamed()
    {
        var named = new[] { AutoConnectPolicy.Never, AutoConnectPolicy.JustOnce, AutoConnectPolicy.Always };
        Assert.Equal(named, Enum.GetValues<AutoConnectPolicy>());
    }

    [Fact]
    public void ConnectionMode_TheOfferedMembersAreExactlyTheNamedOnes()
    {
        var offered = Enum.GetValues<ConnectionMode>().Where(mode => !IsObsolete(mode));
        Assert.Equal([ConnectionMode.AdvertiseOnly, ConnectionMode.DiscoverOnly], offered);
    }

    [Fact]
    public void TheConverters_LeaveOtherValuesAlone()
    {
        Assert.Null(ConnectionModeLabel.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(AutoConnectPolicyLabel.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => ConnectionModeLabel.Instance.ConvertBack("x", typeof(ConnectionMode), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => AutoConnectPolicyLabel.Instance.ConvertBack("x", typeof(AutoConnectPolicy), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsObsolete<T>(T member)
        where T : struct, Enum =>
        typeof(T).GetField(member.ToString())!.GetCustomAttribute<ObsoleteAttribute>() is not null;
}
