using Foundation;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Notifications;
using UserNotifications;

namespace Sendspin.Platform.MacOS.Notifications;

/// <summary>
/// Desktop notifications through <see cref="UNUserNotificationCenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This needs an app bundle, and fails hard without one.</strong> Touching
/// <c>UNUserNotificationCenter.Current</c> from a bare executable raises
/// <c>bundleProxyForCurrentProcess is nil</c>. The same binary inside an <c>.app</c> works, and it
/// does not need to be notarized or even signed with a real identity — ad-hoc is enough. So
/// <see cref="InitializeAsync"/> looks for a bundle first and reports
/// <see cref="IsAvailable"/> false with a logged reason rather than throwing, which is what the
/// contract asks for and what keeps a missing facility from stopping the player.
/// </para>
/// <para>
/// <strong><c>dotnet run</c> will fail this check</strong>, and so will running
/// <c>Contents/MacOS/&lt;binary&gt;</c> directly. The discriminator is the launch mechanism, not
/// the file layout: launched through LaunchServices — <c>open Sendspin.app</c>, or from Finder —
/// the app gets a real permission dialog; exec'd directly, authorisation comes back with
/// "Notifications are not allowed" even though the bundle is right there. Testing this therefore
/// means launching the <c>.app</c>, not the binary inside it.
/// </para>
/// <para>
/// <strong>Logging.</strong> <c>Console</c> output is swallowed once the process is hosted inside a
/// macios <c>.app</c>, so the log lines here are only visible through a file sink. During bring-up
/// that is the only way to see why notifications are off.
/// </para>
/// </remarks>
public sealed class UserNotificationService : INotificationService
{
    /// <summary>
    /// Identifier for the one notification this service keeps on screen.
    /// </summary>
    /// <remarks>
    /// A fixed id, so a new track's notification replaces the previous one instead of stacking. A
    /// player that queues a toast per track buries everything else in Notification Centre.
    /// </remarks>
    private const string NotificationIdentifier = "io.sendspin.player.nowplaying";

    private readonly ILogger<UserNotificationService> _logger;

    private UNUserNotificationCenter? _center;
    private bool _isAvailable;
    private bool _isDisposed;

    public UserNotificationService(ILogger<UserNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsAvailable => Volatile.Read(ref _isAvailable);

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isDisposed)
        {
            return;
        }

        var bundleIdentifier = NSBundle.MainBundle.BundleIdentifier;
        if (string.IsNullOrEmpty(bundleIdentifier))
        {
            _logger.LogWarning(
                "Notifications are unavailable: this process has no app bundle, so " +
                "UNUserNotificationCenter would fail with 'bundleProxyForCurrentProcess is nil'. " +
                "Run the packaged .app instead of the bare executable.");
            return;
        }

        try
        {
            var center = UNUserNotificationCenter.Current;
            var (granted, error) = await center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound).ConfigureAwait(false);

            if (error is not null)
            {
                // Denial and refusal are both normal, so neither throws. This is the branch that
                // catches a directly-exec'd bundle: it reports "Notifications are not allowed"
                // even though the bundle identifier resolved fine above.
                _logger.LogWarning(
                    "Notifications are unavailable: {Reason}. Launching the .app through Finder or " +
                    "`open` rather than exec'ing Contents/MacOS directly is what produces a real " +
                    "permission prompt.", error.LocalizedDescription);
                return;
            }

            if (!granted)
            {
                _logger.LogInformation(
                    "Notifications are unavailable: the user has not granted permission for bundle {Bundle}",
                    bundleIdentifier);
                return;
            }

            _center = center;
            Volatile.Write(ref _isAvailable, true);
            _logger.LogInformation("User notifications authorised for bundle {Bundle}", bundleIdentifier);
        }
        catch (ObjCRuntime.ObjCException ex)
        {
            // The bundle check above covers the documented failure, but UserNotifications raises
            // Objective-C exceptions for packaging states that are not enumerable from here, and
            // this method is contractually not allowed to throw.
            _logger.LogWarning(ex, "Notifications are unavailable: UserNotifications refused to start");
        }
    }

    /// <inheritdoc/>
    public async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var center = _center;
        if (!IsAvailable || center is null || _isDisposed)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var content = new UNMutableNotificationContent
        {
            Title = request.Title,
            Body = request.Body ?? string.Empty,
            ThreadIdentifier = NotificationIdentifier
        };

        var attachment = TryAttachArtwork(request.ArtworkFilePath);
        if (attachment is not null)
        {
            content.Attachments = [attachment];
        }

        // A trigger of nil means deliver immediately.
        using var notification = UNNotificationRequest.FromIdentifier(NotificationIdentifier, content, null!);

        try
        {
            await center.AddNotificationRequestAsync(notification).ConfigureAwait(false);
        }
        catch (NSErrorException ex)
        {
            _logger.LogWarning(ex, "Could not deliver the notification '{Title}'", request.Title);
        }
        finally
        {
            attachment?.Dispose();
        }
    }

    /// <inheritdoc/>
    public Task WithdrawAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _center?.RemoveDeliveredNotifications([NotificationIdentifier]);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        Volatile.Write(ref _isAvailable, false);

        // Leaves nothing of ours on screen after the player exits.
        _center?.RemoveDeliveredNotifications([NotificationIdentifier]);
        _center = null;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds an image attachment, or null when there is no usable artwork.
    /// </summary>
    /// <remarks>
    /// An attachment rather than an icon: macOS renders it as the notification's thumbnail, which
    /// is the only place album art can appear in this surface.
    /// </remarks>
    private UNNotificationAttachment? TryAttachArtwork(string? artworkFilePath)
    {
        if (string.IsNullOrEmpty(artworkFilePath) || !File.Exists(artworkFilePath))
        {
            return null;
        }

        var url = NSUrl.FromFilename(artworkFilePath);

        // Empty options: macOS infers the type from the file and its default thumbnail behaviour
        // is the one wanted here.
        var attachment = UNNotificationAttachment.FromIdentifier(
            "artwork", url, new UNNotificationAttachmentOptions(), out var error);

        if (error is not null || attachment is null)
        {
            _logger.LogDebug("Artwork at {Path} could not be attached: {Reason}",
                artworkFilePath, error?.LocalizedDescription ?? "unknown");
            return null;
        }

        return attachment;
    }
}
