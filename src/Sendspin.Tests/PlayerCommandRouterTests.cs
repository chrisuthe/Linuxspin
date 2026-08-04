using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Control;
using Sendspin.Core.MediaSession;
using Sendspin.SDK.Protocol.Messages;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the single command path.
/// </summary>
/// <remarks>
/// This is the test that backs the requirement that inbound media-session intents travel the same
/// route as a click in the window. The route is shared by construction — both origins call
/// <see cref="PlayerCommandRouter.RouteAsync"/> — and these tests assert what that route produces,
/// including for the relative commands that can only be resolved against the server's state.
/// </remarks>
public sealed class PlayerCommandRouterTests
{
    [Theory]
    [InlineData(MediaSessionIntent.Play, "play")]
    [InlineData(MediaSessionIntent.Pause, "pause")]
    [InlineData(MediaSessionIntent.Stop, "stop")]
    [InlineData(MediaSessionIntent.Next, "next")]
    [InlineData(MediaSessionIntent.Previous, "previous")]
    public async Task Route_ForwardsAbsoluteTransportCommands(MediaSessionIntent intent, string expected)
    {
        var (router, sink) = Build();

        await router.RouteAsync(new MediaSessionIntentEventArgs(intent));

        Assert.Equal([expected], sink.Commands);
    }

    /// <summary>
    /// Toggle must be resolved against the server's state, not the caller's idea of it.
    /// </summary>
    /// <remarks>
    /// Wired headphones send a toggle while Bluetooth sends discrete play and pause, so a session
    /// that only handled the discrete pair would lose the wired case entirely.
    /// </remarks>
    [Theory]
    [InlineData(MediaPlaybackStatus.Playing, "pause")]
    [InlineData(MediaPlaybackStatus.Paused, "play")]
    [InlineData(MediaPlaybackStatus.Stopped, "play")]
    public async Task Route_ResolvesTogglePlayPauseFromServerState(MediaPlaybackStatus status, string expected)
    {
        var (router, sink) = Build();
        sink.CurrentState = new MediaSessionState { Status = status };

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.TogglePlayPause));

        Assert.Equal([expected], sink.Commands);
    }

    [Theory]
    [InlineData(false, "shuffle")]
    [InlineData(true, "unshuffle")]
    public async Task Route_ResolvesShuffleFromServerState(bool shuffling, string expected)
    {
        var (router, sink) = Build();
        sink.CurrentState = new MediaSessionState { Shuffle = shuffling };

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.ToggleShuffle));

        Assert.Equal([expected], sink.Commands);
    }

    [Theory]
    [InlineData(MediaRepeatMode.Off, "repeat_all")]
    [InlineData(MediaRepeatMode.All, "repeat_one")]
    [InlineData(MediaRepeatMode.One, "repeat_off")]
    public async Task Route_CyclesRepeatFromServerState(MediaRepeatMode mode, string expectedCommand)
    {
        var (router, sink) = Build();
        sink.CurrentState = new MediaSessionState { Repeat = mode };

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.CycleRepeat));

        // Assert against the SDK's own constant rather than a literal, so a protocol rename shows
        // up here rather than at runtime against a real server.
        var expected = mode switch
        {
            MediaRepeatMode.Off => Commands.RepeatAll,
            MediaRepeatMode.All => Commands.RepeatOne,
            _ => Commands.RepeatOff
        };

        Assert.Equal([expected], sink.Commands);
        Assert.Equal(expectedCommand, expected);
    }

    [Fact]
    public async Task Route_ClampsAndForwardsVolume()
    {
        var (router, sink) = Build();

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.SetVolume, Volume: 55));
        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.SetVolume, Volume: 250));
        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.SetVolume, Volume: -20));

        Assert.Equal([55, 100, 0], sink.Volumes);
    }

    /// <summary>
    /// An intent whose argument is missing must be dropped, not turned into a guess.
    /// </summary>
    [Fact]
    public async Task Route_IgnoresVolumeIntentWithNoValue()
    {
        var (router, sink) = Build();

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.SetVolume));

        Assert.Empty(sink.Volumes);
        Assert.Empty(sink.Commands);
    }

    [Fact]
    public async Task Route_ForwardsMute()
    {
        var (router, sink) = Build();

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.SetMute, Muted: true));

        Assert.Equal([true], sink.Mutes);
    }

    /// <summary>
    /// The player role has no seek command, so the intent is declined rather than mistranslated.
    /// </summary>
    [Fact]
    public async Task Route_DeclinesSeek()
    {
        var (router, sink) = Build();

        await router.RouteAsync(
            new MediaSessionIntentEventArgs(MediaSessionIntent.Seek, Position: TimeSpan.FromSeconds(30)));

        Assert.Empty(sink.Commands);
    }

    /// <summary>
    /// A media key pressed while disconnected is ordinary, not an error.
    /// </summary>
    [Fact]
    public async Task Route_WhileDisconnected_SendsNothingAndDoesNotThrow()
    {
        var (router, sink) = Build();
        sink.CanSend = false;

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.Play));

        Assert.Empty(sink.Commands);
    }

    /// <summary>
    /// Raise and quit are the app's to answer, and must not be sent to the server.
    /// </summary>
    [Theory]
    [InlineData(MediaSessionIntent.Raise, LocalAction.Raise)]
    [InlineData(MediaSessionIntent.Quit, LocalAction.Quit)]
    public async Task Route_RaisesLocalActionsRatherThanSendingThem(
        MediaSessionIntent intent, LocalAction expected)
    {
        var (router, sink) = Build();
        var observed = new List<LocalAction>();
        router.LocalActionRequested += (_, action) => observed.Add(action);

        await router.RouteAsync(new MediaSessionIntentEventArgs(intent));

        Assert.Equal([expected], observed);
        Assert.Empty(sink.Commands);
    }

    /// <summary>
    /// Local actions must work even with no server, since quitting is how the user closes an app
    /// that failed to connect.
    /// </summary>
    [Fact]
    public async Task Route_RaisesLocalActionsWhileDisconnected()
    {
        var (router, _) = Build();
        var observed = new List<LocalAction>();
        router.LocalActionRequested += (_, action) => observed.Add(action);

        await router.RouteAsync(new MediaSessionIntentEventArgs(MediaSessionIntent.Quit));

        Assert.Equal([LocalAction.Quit], observed);
    }

    private static (PlayerCommandRouter Router, RecordingCommandSink Sink) Build()
    {
        var sink = new RecordingCommandSink();
        return (new PlayerCommandRouter(sink, NullLogger<PlayerCommandRouter>.Instance), sink);
    }
}
