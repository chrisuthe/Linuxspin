using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for what this player advertises in <c>client/hello</c>.
/// </summary>
/// <remarks>
/// The advertisement is a promise, not a hint: the spec makes <c>buffer_capacity</c> a hard
/// per-player byte limit that servers fill toward, so a wrong figure is not a tuning miss — it
/// either starves the stream or invites the server to send audio that is discarded unplayed.
/// </remarks>
public sealed class PlayerCapabilitiesTests
{
    private static SDK.Client.ClientCapabilities Build() =>
        PlayerCapabilities.Build(
            new PlayerSettings { ClientId = "id", PlayerName = "Kitchen" },
            device: null,
            softwareVersion: "1.0.0");

    /// <summary>
    /// The advertised capacity must be the SDK's derived figure, not the 8 000 this repo used to
    /// set.
    /// </summary>
    /// <remarks>
    /// The old constant was named and commented as milliseconds, but the field is compressed
    /// bytes. 8 000 bytes is about one second of Opus at the SDK's assumed rate — a starved
    /// advertisement that no amount of buffer would fix, because the server never sends past what
    /// it was told. Leaving the property unset makes the SDK derive it from its own 30 s decoded
    /// buffer and the formats below, which is the only way the two sides agree.
    /// </remarks>
    [Fact]
    public void BufferCapacity_IsDerivedByTheSdkRatherThanAdvertisedAsMilliseconds()
    {
        var capabilities = Build();

        Assert.NotEqual(8_000, capabilities.BufferCapacity);

        // The derivation is (N-1)/N of what 30 s of the thinnest advertised format occupies. The
        // thinnest is a bitrate-less Opus entry, which the SDK values at its conservative 64 kbps
        // fallback: 8 000 B/s x 30 s x 4/5 = 192 000 bytes. Asserted exactly, because the point of
        // the change is that this number is now derived from something real — a drift in it means
        // the format list or the SDK's assumptions moved and the advertisement moved with them.
        Assert.Equal(192_000, capabilities.BufferCapacity);
    }

    /// <summary>
    /// The advertisement has to be a plausible number of bytes for what is actually offered.
    /// </summary>
    /// <remarks>
    /// The bound that matters is the one the old value failed: whatever the server may send must
    /// decode to a useful amount of audio in the *thinnest* format offered, because the server
    /// picks the format and the promise has to hold either way.
    /// </remarks>
    [Fact]
    public void BufferCapacity_HoldsAUsefulAmountOfTheThinnestAdvertisedFormat()
    {
        var capabilities = Build();

        // 64 kbps is the SDK's fallback for the bitrate-less Opus entries this build advertises.
        const int ThinnestBytesPerSecond = 64_000 / 8;
        var seconds = capabilities.BufferCapacity / (double)ThinnestBytesPerSecond;

        Assert.InRange(seconds, 10.0, 30.0);
    }

    /// <summary>
    /// The Opus entries carry no declared bitrate, which is what pins the derivation to the SDK's
    /// fallback rather than to something this build asked for.
    /// </summary>
    /// <remarks>
    /// Asserted rather than left implicit because it is the one assumption the advertised figure
    /// rests on. Declaring <see cref="AudioFormat.Bitrate"/> would tighten it and is deliberately
    /// out of scope here — so if someone declares it later, this test is where they find out that
    /// the number above moves with it.
    /// </remarks>
    [Fact]
    public void OpusFormats_DeclareNoBitrate()
    {
        var opus = Build().AudioFormats!.Where(f => f.Codec == AudioCodecs.Opus).ToList();

        Assert.NotEmpty(opus);
        Assert.All(opus, format => Assert.Null(format.Bitrate));
    }
}
