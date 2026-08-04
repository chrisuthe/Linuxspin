using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.Portals;

/// <summary>
/// The shared mechanics of an <c>xdg-desktop-portal</c> request object.
/// </summary>
/// <remarks>
/// A portal call returns immediately with the path of a <c>org.freedesktop.portal.Request</c>
/// object and answers later with a <c>Response</c> signal on it. Subscribing after the call has
/// returned is a race the caller loses on a portal that answers without asking the user, so the
/// documented recipe is to pass a <c>handle_token</c>, predict the path from it, and subscribe
/// first. That prediction is what this type exists for.
/// </remarks>
internal static class PortalRequest
{
    public const string Destination = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>Response code meaning the request was granted.</summary>
    public const uint SuccessResponse = 0;

    /// <summary>
    /// Mints a fresh <c>handle_token</c>. Must be a valid D-Bus path element, so it is hex.
    /// </summary>
    public static string NewToken() => "sendspin" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Predicts the request object's path for a token, per the portal specification:
    /// <c>/org/freedesktop/portal/desktop/request/SENDER/TOKEN</c>, where SENDER is the caller's
    /// unique name with the leading colon removed and dots replaced by underscores.
    /// </summary>
    public static string PredictPath(string uniqueName, string token)
    {
        var sender = uniqueName.TrimStart(':').Replace('.', '_');
        return $"{ObjectPath}/request/{sender}/{token}";
    }

    /// <summary>
    /// Reads a <c>Response(u, a{sv})</c> body, keeping only the response code.
    /// </summary>
    public static uint ReadResponseCode(Message message, object? state) => message.GetBodyReader().ReadUInt32();

    /// <summary>
    /// Builds a <c>Request.Close()</c> call, which is how a portal request — and with it whatever
    /// it was holding, such as an idle inhibition — is ended.
    /// </summary>
    public static MessageBuffer CreateCloseMessage(DBusConnection connection, string requestPath)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, requestPath, RequestInterface, "Close", null, MessageFlags.None);
        return writer.CreateMessage();
    }
}
