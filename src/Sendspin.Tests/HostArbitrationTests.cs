using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests the arbitration contract the player's adopt/release wiring depends on (SDK #253).
/// </summary>
/// <remarks>
/// <para>
/// The defect these guard against: a server dialling in while this player was already playing from
/// a session it dialled itself used to be arbitrated as "no existing connection" — accepted,
/// registered and announced — which reset the shared clock synchroniser and audio pipeline out from
/// under a stream that was still playing. <c>AdoptClientInitiated</c> is what gives the host
/// something to arbitrate against.
/// </para>
/// <para>
/// <b>What is and is not covered.</b> These drive the SDK's host service directly. That is
/// deliberate rather than a shortcut: <c>SendspinHostService</c> and <c>SendspinClientService</c>
/// are both sealed and <c>AdoptClientInitiated</c> takes the concrete client type, so there is no
/// seam to observe the calls through — and reaching the calls inside
/// <see cref="SendspinPlayerService"/> means <c>StartAdvertisingAsync</c>, which binds a TCP
/// listener and joins an mDNS multicast group. So what is asserted here is the ownership contract
/// the wiring rests on: that adoption does not register the session as a connected server, and that
/// release leaves nothing behind. That the player calls them at the right moments is not covered by
/// a test, and is read from <c>ConnectAsync</c> and <c>DisconnectAsync</c>.
/// </para>
/// </remarks>
public sealed class HostArbitrationTests
{
    private const string ServerId = "server-7";

    /// <summary>
    /// Adoption must not make the host think it accepted the session.
    /// </summary>
    /// <remarks>
    /// This is the property the player's teardown depends on: the SDK never disconnects or disposes
    /// an adopted client, so <see cref="SendspinPlayerService"/> keeps disposing its own client
    /// exactly as before. If adoption registered the session, both would try.
    /// </remarks>
    [Fact]
    public async Task AdoptClientInitiated_DoesNotRegisterTheSessionAsAConnectedServer()
    {
        await using var host = CreateHost();
        await using var client = CreateClient(out _);

        var announced = new List<string>();
        host.ServerConnected += (_, info) => announced.Add(info.ServerId);

        host.AdoptClientInitiated(client, ServerId);

        Assert.Empty(host.ConnectedServers);
        Assert.Empty(announced);
    }

    /// <summary>
    /// Release must leave the host holding nothing, and must not touch the client.
    /// </summary>
    /// <remarks>
    /// The failure this rules out is the mirror of #253: an adoption left in place would keep
    /// refusing every incoming server on behalf of a session that has already gone.
    /// </remarks>
    [Fact]
    public async Task ReleaseClientInitiated_LeavesNothingAdoptedAndTheClientUntouched()
    {
        await using var host = CreateHost();
        await using var client = CreateClient(out var connection);

        var disconnected = new List<string>();
        host.ServerDisconnected += (_, id) => disconnected.Add(id);

        host.AdoptClientInitiated(client, ServerId);
        host.ReleaseClientInitiated(ServerId);

        // Ownership stays with the caller: releasing arbitration is not a disconnect.
        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Empty(host.ConnectedServers);

        // Adoption is invisible to the connected-server events, so release raises none either.
        Assert.Empty(disconnected);
    }

    /// <summary>
    /// Releasing an id that was never adopted must be a no-op.
    /// </summary>
    /// <remarks>
    /// The player calls release from both <c>DisconnectAsync</c> and its disposal path, and a
    /// dropped session releases itself, so release genuinely does run twice. It has to be safe.
    /// </remarks>
    [Fact]
    public async Task ReleaseClientInitiated_IsSafeWhenNothingWasAdopted()
    {
        await using var host = CreateHost();

        host.ReleaseClientInitiated(ServerId);
        host.ReleaseClientInitiated(ServerId);

        Assert.Empty(host.ConnectedServers);
    }

    private static SendspinHostService CreateHost() =>
        new(NullLoggerFactory.Instance,
            Capabilities(),
            new ListenerOptions(),
            new AdvertiserOptions { ClientId = "client-1", PlayerName = "Kitchen" },
            new InertAudioPipeline(),
            new ConvergedClockSynchronizer(),
            lastPlayedServerId: null);

    private static SendspinClientService CreateClient(out FakeConnection connection)
    {
        connection = new FakeConnection();

        return new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new ConvergedClockSynchronizer(),
            Capabilities(),
            new InertAudioPipeline(),
            new InMemoryStaticDelayStore());
    }

    private static ClientCapabilities Capabilities() =>
        PlayerCapabilities.Build(
            new PlayerSettings { ClientId = "client-1", PlayerName = "Kitchen" },
            device: null,
            softwareVersion: "1.0.0");
}
