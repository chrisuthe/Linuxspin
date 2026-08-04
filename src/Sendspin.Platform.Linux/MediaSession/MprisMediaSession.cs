using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;
using Sendspin.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.MediaSession;

/// <summary>
/// Publishes this player as an MPRIS2 media player, and turns inbound MPRIS calls into
/// <see cref="MediaSessionIntent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written dispatch over <c>Tmds.DBus.Protocol</c>, which is a wire-level library: there is
/// no proxy generation here, and none is wanted, because the reflection-based <c>Tmds.DBus</c>
/// would not survive a trimmed publish.
/// </para>
/// <para>
/// Several details below are what decide whether the player appears in a shell at all, so they
/// are called out where they are implemented rather than summarised here: the trailing dot in the
/// bus name, <c>CanControl</c>, a non-empty <c>Identity</c>, <c>Properties.GetAll</c> never
/// erroring, and <c>PropertiesChanged</c> carrying an empty invalidated-properties array.
/// </para>
/// <para>
/// <strong>Media keys need nothing beyond this.</strong> There is deliberately no
/// <c>org.gnome.SettingsDaemon.MediaKeys</c> implementation and no GlobalShortcuts portal use:
/// GNOME removed its media-keys API in 2021 with the message "superseded by MPRIS", and MPRIS is
/// the mechanism on both shells.
/// </para>
/// <para>
/// <strong>This class never touches audio.</strong> Every inbound call raises
/// <see cref="IntentReceived"/> and returns. Transport state is the server's, so a shell's button
/// has to travel the same controller path as a click in the app.
/// </para>
/// </remarks>
public sealed class MprisMediaSession : IMediaSession
{
    /// <summary>
    /// Prefix for the bus name, trailing dot included.
    /// </summary>
    /// <remarks>
    /// Both shells find players by matching bus names against <c>org.mpris.MediaPlayer2.</c> —
    /// <em>with</em> the dot. A player that owns the bare <c>org.mpris.MediaPlayer2</c> is
    /// invisible to both.
    /// </remarks>
    private const string BusNamePrefix = "org.mpris.MediaPlayer2.";

    private const string ApplicationId = "io.sendspin.client";
    private const string SessionObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string IntrospectableInterface = "org.freedesktop.DBus.Introspectable";
    private const string PeerInterface = "org.freedesktop.DBus.Peer";

    /// <summary>
    /// The name a shell shows for this player. Must not be empty: KDE treats an empty
    /// <c>Identity</c> as a malformed player.
    /// </summary>
    private const string IdentityName = "Sendspin";

    private const string ErrorFailed = "org.freedesktop.DBus.Error.Failed";
    private const string ErrorInvalidArgs = "org.freedesktop.DBus.Error.InvalidArgs";
    private const string ErrorNotSupported = "org.freedesktop.DBus.Error.NotSupported";

    /// <summary>
    /// How far the reported position may drift from where it was expected before the change is
    /// treated as a seek rather than as ordinary playback.
    /// </summary>
    private const int SeekToleranceMilliseconds = 1_500;

    private readonly SessionBus _sessionBus;
    private readonly ILogger<MprisMediaSession> _logger;
    private readonly Lock _publishGate = new();
    private readonly Dictionary<string, VariantValue> _rootProperties;

    private Dictionary<string, VariantValue> _playerProperties;
    private MediaSessionState _state = MediaSessionState.Idle;
    private long _positionAnchorTicks = Environment.TickCount64;
    private DBusConnection? _connection;
    private string? _busName;
    private bool _isActive;
    private bool _disposed;

    public MprisMediaSession(SessionBus sessionBus, ILogger<MprisMediaSession> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionBus);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionBus = sessionBus;
        _logger = logger;
        _rootProperties = BuildRootProperties();
        _playerProperties = BuildPlayerProperties(_state);
    }

    /// <inheritdoc/>
    public event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <inheritdoc/>
    public bool IsActive => _isActive;

    /// <inheritdoc/>
    /// <remarks>
    /// Never throws. No session bus, or a bus that already has an owner for our name and refuses
    /// the instance-qualified fallback, leaves <see cref="IsActive"/> false with the reason
    /// logged — the common case inside a container or over ssh.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _isActive)
        {
            return;
        }

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            _logger.LogInformation("MPRIS not published: no session bus");
            return;
        }

        try
        {
            // Export before owning the name, so no call can arrive at an unhandled path.
            connection.AddMethodHandler(new SessionPathHandler(this));

            var busName = BusNamePrefix + ApplicationId;

            if (!await connection.TryRequestNameAsync(busName, RequestNameOptions.None).ConfigureAwait(false))
            {
                // The specification's answer to a second instance. Still matches the prefix both
                // shells filter on, so the second player is visible rather than silently absent.
                var instanceName = $"{busName}.instance{Environment.ProcessId}";

                if (!await connection.TryRequestNameAsync(instanceName, RequestNameOptions.None).ConfigureAwait(false))
                {
                    connection.RemoveMethodHandler(SessionObjectPath);
                    _logger.LogWarning(
                        "MPRIS not published: could own neither {BusName} nor {InstanceName}",
                        busName, instanceName);
                    return;
                }

                busName = instanceName;
            }

            _connection = connection;
            _busName = busName;
            _isActive = true;

            _logger.LogInformation("MPRIS published as {BusName} on {ObjectPath}", busName, SessionObjectPath);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or SocketException)
        {
            _logger.LogWarning(ex, "MPRIS could not be published");
        }
    }

    /// <inheritdoc/>
    public void Publish(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_disposed)
        {
            return;
        }

        Dictionary<string, VariantValue> changed;
        bool seeked;
        TimeSpan seekedTo;

        lock (_publishGate)
        {
            var previous = _state;
            var properties = BuildPlayerProperties(state);

            seeked = IsSeek(previous, state);
            seekedTo = state.Position;
            changed = DiffPlayerProperties(previous, state, properties);

            _state = state;
            _positionAnchorTicks = Environment.TickCount64;
            Volatile.Write(ref _playerProperties, properties);
        }

        if (!_isActive)
        {
            return;
        }

        if (changed.Count > 0)
        {
            EmitPropertiesChanged(PlayerInterface, changed);
        }

        if (seeked)
        {
            EmitSeeked(seekedTo);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _isActive = false;

        // The connection itself belongs to SessionBus, so only this object's export is withdrawn.
        _connection?.RemoveMethodHandler(SessionObjectPath);
        _connection = null;

        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, VariantValue> BuildRootProperties() => new(StringComparer.Ordinal)
    {
        ["CanQuit"] = VariantValue.Bool(true),
        ["CanRaise"] = VariantValue.Bool(true),
        ["HasTrackList"] = VariantValue.Bool(false),
        ["Identity"] = VariantValue.String(IdentityName),

        // The installed desktop file's basename, which is how a shell finds the icon to show
        // beside the player.
        ["DesktopEntry"] = VariantValue.String(ApplicationId),

        // This player is fed by a server; it opens nothing itself, so it advertises no schemes
        // and no MIME types and refuses OpenUri.
        ["SupportedUriSchemes"] = VariantValue.Array(Array.Empty<string>()),
        ["SupportedMimeTypes"] = VariantValue.Array(Array.Empty<string>())
    };

    private static Dictionary<string, VariantValue> BuildPlayerProperties(MediaSessionState state) =>
        new(StringComparer.Ordinal)
        {
            ["PlaybackStatus"] = VariantValue.String(MediaSessionMapper.ToMprisPlaybackStatus(state.Status)),
            ["LoopStatus"] = VariantValue.String(MediaSessionMapper.ToMprisLoopStatus(state.Repeat)),
            ["Rate"] = VariantValue.Double(1.0),
            ["MinimumRate"] = VariantValue.Double(1.0),
            ["MaximumRate"] = VariantValue.Double(1.0),
            ["Shuffle"] = VariantValue.Bool(state.Shuffle),
            ["Metadata"] = MprisMetadata.Build(state),

            // MPRIS volume is a linear 0.0-1.0 multiplier, and the protocol volume is 0-100.
            // Muted reports zero because that is what is actually audible.
            ["Volume"] = VariantValue.Double(state.Muted ? 0.0 : state.Volume / 100.0),

            ["CanGoNext"] = VariantValue.Bool(state.CanGoNext),
            ["CanGoPrevious"] = VariantValue.Bool(state.CanGoPrevious),

            // Play and pause are always offered. A shell that sees CanPlay false hides the
            // button, and a hidden button is a media key that goes nowhere; the controller drops
            // a command the server cannot currently honour.
            ["CanPlay"] = VariantValue.Bool(true),
            ["CanPause"] = VariantValue.Bool(true),

            ["CanSeek"] = VariantValue.Bool(state.CanSeek && !state.IsLive),

            // KDE gates every control, and its media-key daemon, on this. GNOME never reads it.
            ["CanControl"] = VariantValue.Bool(true)
        };

    /// <summary>
    /// Builds the <c>changed_properties</c> map for a state transition.
    /// </summary>
    /// <remarks>
    /// Diffed against the previous state rather than emitting everything each time, so a
    /// once-per-second position update does not broadcast the whole property set to the bus.
    /// <c>Metadata</c> is always sent whole when any part of it changed: a partial map makes KDE
    /// discard the entry. <c>Position</c> is never included — the specification excludes it, and
    /// <c>Seeked</c> is how a jump is announced.
    /// </remarks>
    private static Dictionary<string, VariantValue> DiffPlayerProperties(
        MediaSessionState previous,
        MediaSessionState current,
        Dictionary<string, VariantValue> properties)
    {
        var changed = new Dictionary<string, VariantValue>(StringComparer.Ordinal);

        if (previous.Status != current.Status)
        {
            changed["PlaybackStatus"] = properties["PlaybackStatus"];
        }

        if (previous.Repeat != current.Repeat)
        {
            changed["LoopStatus"] = properties["LoopStatus"];
        }

        if (previous.Shuffle != current.Shuffle)
        {
            changed["Shuffle"] = properties["Shuffle"];
        }

        if (previous.Volume != current.Volume || previous.Muted != current.Muted)
        {
            changed["Volume"] = properties["Volume"];
        }

        if (previous.CanGoNext != current.CanGoNext)
        {
            changed["CanGoNext"] = properties["CanGoNext"];
        }

        if (previous.CanGoPrevious != current.CanGoPrevious)
        {
            changed["CanGoPrevious"] = properties["CanGoPrevious"];
        }

        if (previous.CanSeek != current.CanSeek || previous.IsLive != current.IsLive)
        {
            changed["CanSeek"] = properties["CanSeek"];
        }

        if (MprisMetadata.Differs(previous, current))
        {
            changed["Metadata"] = properties["Metadata"];
        }

        return changed;
    }

    /// <summary>
    /// Whether the new position is too far from where playback would have reached on its own.
    /// </summary>
    private bool IsSeek(MediaSessionState previous, MediaSessionState current)
    {
        if (previous.TrackIdentity != current.TrackIdentity)
        {
            return false;
        }

        var elapsed = previous.Status == MediaPlaybackStatus.Playing
            ? TimeSpan.FromMilliseconds(Environment.TickCount64 - _positionAnchorTicks)
            : TimeSpan.Zero;

        var expected = previous.Position + elapsed;
        return Math.Abs((current.Position - expected).TotalMilliseconds) > SeekToleranceMilliseconds;
    }

    /// <summary>
    /// Gets the current position, extrapolated from the last published state.
    /// </summary>
    /// <remarks>
    /// Extrapolated rather than served from the snapshot because a shell reads <c>Position</c>
    /// whenever it wants to move its progress bar, and a value that only moves when the server
    /// sends an update makes the bar visibly stutter.
    /// </remarks>
    private long CurrentPositionMicroseconds()
    {
        MediaSessionState state;
        long anchorTicks;

        lock (_publishGate)
        {
            state = _state;
            anchorTicks = _positionAnchorTicks;
        }

        var position = state.Position;

        if (state.Status == MediaPlaybackStatus.Playing)
        {
            position += TimeSpan.FromMilliseconds(Environment.TickCount64 - anchorTicks);
        }

        if (state.Duration is { } duration && position > duration)
        {
            position = duration;
        }

        return (long)Math.Max(0, position.TotalMicroseconds);
    }

    /// <summary>
    /// Returns the property map for an interface, and an empty map for anything else.
    /// </summary>
    /// <remarks>
    /// Empty rather than an error, deliberately: KDE deletes the whole player container when
    /// <c>GetAll</c> errors, so there is no interface name worth failing for.
    /// </remarks>
    private Dictionary<string, VariantValue> PropertiesFor(string interfaceName)
    {
        if (string.Equals(interfaceName, RootInterface, StringComparison.Ordinal))
        {
            return _rootProperties;
        }

        if (!string.Equals(interfaceName, PlayerInterface, StringComparison.Ordinal))
        {
            return new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        }

        // Copied so the live position can be added without mutating the published snapshot.
        var properties = new Dictionary<string, VariantValue>(Volatile.Read(ref _playerProperties), StringComparer.Ordinal)
        {
            ["Position"] = VariantValue.Int64(CurrentPositionMicroseconds())
        };

        return properties;
    }

    private void Dispatch(MethodContext context)
    {
        switch (context.Request.InterfaceAsString)
        {
            case PropertiesInterface:
                HandleProperties(context);
                break;

            case IntrospectableInterface:
                HandleIntrospect(context);
                break;

            case PeerInterface:
                HandlePeer(context);
                break;

            case RootInterface:
                HandleRoot(context);
                break;

            case PlayerInterface:
                HandlePlayer(context);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandleProperties(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();

        switch (context.Request.MemberAsString)
        {
            case "GetAll":
            {
                using var writer = context.CreateReplyWriter("a{sv}");
                writer.WriteDictionary(PropertiesFor(reader.ReadString()));
                context.Reply(writer.CreateMessage());
                break;
            }

            case "Get":
            {
                var interfaceName = reader.ReadString();
                var propertyName = reader.ReadString();

                if (!PropertiesFor(interfaceName).TryGetValue(propertyName, out var value))
                {
                    context.ReplyError(ErrorInvalidArgs, $"No such property {interfaceName}.{propertyName}");
                    break;
                }

                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariant(value);
                context.Reply(writer.CreateMessage());
                break;
            }

            case "Set":
                SetProperty(context, ref reader);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void SetProperty(MethodContext context, ref Reader reader)
    {
        var interfaceName = reader.ReadString();
        var propertyName = reader.ReadString();
        var value = reader.ReadVariantValue();

        if (!string.Equals(interfaceName, PlayerInterface, StringComparison.Ordinal))
        {
            context.ReplyError(ErrorInvalidArgs, $"No writable property on {interfaceName}");
            return;
        }

        switch (propertyName)
        {
            case "Volume":
                RaiseIntent(new MediaSessionIntentEventArgs(
                    MediaSessionIntent.SetVolume,
                    Volume: (int)Math.Round(Math.Clamp(value.GetDouble(), 0.0, 1.0) * 100)));
                break;

            case "LoopStatus":
                RequestRepeat(MediaSessionMapper.FromMprisLoopStatus(value.GetString()));
                break;

            case "Shuffle":
                RequestShuffle(value.GetBool());
                break;

            case "Rate":
                // MinimumRate and MaximumRate are both 1.0, and the specification says a rate
                // outside that range behaves as though it were clamped. So: accepted, ignored.
                break;

            default:
                context.ReplyError(ErrorInvalidArgs, $"Property {propertyName} is not writable");
                return;
        }

        ReplyEmpty(context);
    }

    /// <summary>
    /// Asks for a specific repeat mode using the one repeat intent that exists.
    /// </summary>
    /// <remarks>
    /// The intent vocabulary has <see cref="MediaSessionIntent.CycleRepeat"/> and no absolute
    /// setter, so a shell picking "Playlist" from "None" is served by one cycle step, which may
    /// land on "Track" instead. The alternative — widening the shared contract for one shell's
    /// menu — is not worth it; the next state the server publishes corrects what the shell shows.
    /// </remarks>
    private void RequestRepeat(MediaRepeatMode requested)
    {
        if (CurrentState().Repeat == requested)
        {
            return;
        }

        RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.CycleRepeat));
    }

    private void RequestShuffle(bool requested)
    {
        if (CurrentState().Shuffle == requested)
        {
            return;
        }

        RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.ToggleShuffle));
    }

    private void HandleRoot(MethodContext context)
    {
        switch (context.Request.MemberAsString)
        {
            case "Raise":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Raise));
                ReplyEmpty(context);
                break;

            case "Quit":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Quit));
                ReplyEmpty(context);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private void HandlePlayer(MethodContext context)
    {
        switch (context.Request.MemberAsString)
        {
            case "Play":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Play));
                ReplyEmpty(context);
                break;

            case "Pause":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Pause));
                ReplyEmpty(context);
                break;

            case "PlayPause":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.TogglePlayPause));
                ReplyEmpty(context);
                break;

            case "Stop":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Stop));
                ReplyEmpty(context);
                break;

            case "Next":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Next));
                ReplyEmpty(context);
                break;

            case "Previous":
                RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Previous));
                ReplyEmpty(context);
                break;

            case "Seek":
                HandleSeek(context);
                break;

            case "SetPosition":
                HandleSetPosition(context);
                break;

            case "OpenUri":
                context.ReplyError(ErrorNotSupported,
                    "This player is fed by a Sendspin server and opens no URIs of its own");
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>
    /// <c>Seek(x Offset)</c>, whose argument is a signed offset in microseconds from the current
    /// position, not an absolute target.
    /// </summary>
    private void HandleSeek(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var offsetMicroseconds = reader.ReadInt64();

        var target = TimeSpan.FromMicroseconds(CurrentPositionMicroseconds() + offsetMicroseconds);
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        RaiseIntent(new MediaSessionIntentEventArgs(MediaSessionIntent.Seek, Position: target));
        ReplyEmpty(context);
    }

    /// <summary>
    /// <c>SetPosition(o TrackId, x Position)</c>, an absolute seek guarded by track identity.
    /// </summary>
    /// <remarks>
    /// The specification requires the call to be ignored when the track id is not the current
    /// track, which is what stops a stale click from seeking whatever started playing since.
    /// </remarks>
    private void HandleSetPosition(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var trackId = reader.ReadObjectPathAsString();
        var positionMicroseconds = reader.ReadInt64();

        var expectedTrackId = MediaSessionMapper.ToMprisTrackId(CurrentState().TrackIdentity);

        if (string.Equals(trackId, expectedTrackId, StringComparison.Ordinal) && positionMicroseconds >= 0)
        {
            RaiseIntent(new MediaSessionIntentEventArgs(
                MediaSessionIntent.Seek,
                Position: TimeSpan.FromMicroseconds(positionMicroseconds)));
        }

        ReplyEmpty(context);
    }

    private void HandleIntrospect(MethodContext context)
    {
        if (!string.Equals(context.Request.MemberAsString, "Introspect", StringComparison.Ordinal))
        {
            context.ReplyUnknownMethodError();
            return;
        }

        ReadOnlyMemory<byte>[] interfaces =
        [
            MprisIntrospection.Root,
            MprisIntrospection.Player,
            IntrospectionXml.DBusProperties,
            IntrospectionXml.DBusIntrospectable,
            IntrospectionXml.DBusPeer
        ];

        context.ReplyIntrospectXml(interfaces, ReadOnlySpan<string>.Empty);
    }

    private void HandlePeer(MethodContext context)
    {
        if (string.Equals(context.Request.MemberAsString, "Ping", StringComparison.Ordinal))
        {
            ReplyEmpty(context);
            return;
        }

        context.ReplyUnknownMethodError();
    }

    private MediaSessionState CurrentState()
    {
        lock (_publishGate)
        {
            return _state;
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private void RaiseIntent(MediaSessionIntentEventArgs args)
    {
        _logger.LogDebug("MPRIS intent {Intent}", args.Intent);
        IntentReceived?.Invoke(this, args);
    }

    /// <summary>
    /// Emits <c>org.freedesktop.DBus.Properties.PropertiesChanged</c>.
    /// </summary>
    /// <remarks>
    /// The invalidated-properties array is always empty. Listing a property as invalidated
    /// instead of sending its value makes KDE drop the player rather than re-read it.
    /// </remarks>
    private void EmitPropertiesChanged(string interfaceName, Dictionary<string, VariantValue> changed)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(null, SessionObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            writer.WriteString(interfaceName);
            writer.WriteDictionary(changed);
            writer.WriteArray(Array.Empty<string>());
            connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex) when (ex is DBusExceptionBase or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not emit PropertiesChanged for {Interface}", interfaceName);
        }
    }

    private void EmitSeeked(TimeSpan position)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(null, SessionObjectPath, PlayerInterface, "Seeked", "x");
            writer.WriteInt64((long)position.TotalMicroseconds);
            connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex) when (ex is DBusExceptionBase or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not emit Seeked");
        }
    }

    /// <summary>
    /// Routes every call on <c>/org/mpris/MediaPlayer2</c> to the session.
    /// </summary>
    private sealed class SessionPathHandler(MprisMediaSession owner) : IPathMethodHandler
    {
        public string Path => SessionObjectPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                owner.Dispatch(context);
            }
            catch (Exception ex)
            {
                // Broad on purpose. An exception out of a method handler tears down the shared
                // connection, taking MPRIS, notifications and the portals with it, so every
                // failure becomes a D-Bus error reply and a log line instead.
                owner._logger.LogWarning(ex, "MPRIS call {Interface}.{Member} failed",
                    context.Request.InterfaceAsString, context.Request.MemberAsString);

                if (!context.ReplySent && !context.NoReplyExpected)
                {
                    context.ReplyError(ErrorFailed, ex.Message);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
