using System.Text;

namespace Sendspin.Platform.Linux.MediaSession;

/// <summary>
/// The <c>org.freedesktop.DBus.Introspectable</c> fragments for the two MPRIS interfaces.
/// </summary>
/// <remarks>
/// Written out rather than generated because a shell reads this to decide what to offer, and
/// because <c>busctl --user introspect</c> is how a human checks the player is correct. Each
/// fragment is one <c>&lt;interface&gt;</c> element; the connection wraps them in a
/// <c>&lt;node&gt;</c> along with the standard interfaces it describes itself.
/// </remarks>
internal static class MprisIntrospection
{
    /// <summary>
    /// <c>org.mpris.MediaPlayer2</c>: what the shell needs to name and raise the application.
    /// </summary>
    /// <remarks>
    /// <c>Fullscreen</c> and <c>CanSetFullscreen</c> are optional in the specification and are
    /// deliberately absent: this player has no fullscreen mode, and advertising one it cannot
    /// honour is worse than not offering it.
    /// </remarks>
    public static ReadOnlyMemory<byte> Root { get; } = Encoding.UTF8.GetBytes(
        """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        """);

    /// <summary>
    /// <c>org.mpris.MediaPlayer2.Player</c>: transport, metadata and the seek surface.
    /// </summary>
    /// <remarks>
    /// <c>Position</c> carries <c>EmitsChangedSignal="false"</c> because the specification excludes
    /// it from <c>PropertiesChanged</c> — a position that emitted on every tick would be a
    /// per-second broadcast to every listener on the bus. Clients poll it and follow
    /// <c>Seeked</c> for jumps.
    /// </remarks>
    public static ReadOnlyMemory<byte> Player { get; } = Encoding.UTF8.GetBytes(
        """
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek">
            <arg name="Offset" type="x" direction="in"/>
          </method>
          <method name="SetPosition">
            <arg name="TrackId" type="o" direction="in"/>
            <arg name="Position" type="x" direction="in"/>
          </method>
          <method name="OpenUri">
            <arg name="Uri" type="s" direction="in"/>
          </method>
          <signal name="Seeked">
            <arg name="Position" type="x"/>
          </signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read">
            <annotation name="org.freedesktop.DBus.Property.EmitsChangedSignal" value="false"/>
          </property>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);
}
