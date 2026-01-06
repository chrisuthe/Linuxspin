using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SendspinClient.Linux.Services.Notifications;

/// <summary>
/// Notification service using notify-send command as a reliable fallback.
/// Uses the desktop notification system without complex D-Bus protocol handling.
/// </summary>
public sealed class DBusNotificationService : INotificationService
{
    private const string AppName = "Sendspin";
    private const int DefaultTimeout = 5000;

    private readonly ILogger<DBusNotificationService> _logger;
    private bool _isInitialized;
    private bool _isDisposed;

    public DBusNotificationService(ILogger<DBusNotificationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(DBusNotificationService));

        // Check if notify-send is available
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "notify-send",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(1000);
            _isInitialized = process?.ExitCode == 0;

            if (_isInitialized)
                _logger.LogInformation("Notification service initialized (using notify-send)");
            else
                _logger.LogWarning("notify-send not available - notifications disabled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for notify-send");
            _isInitialized = false;
        }

        return Task.CompletedTask;
    }

    public async Task ShowTrackChangeAsync(string title, string artist, string? albumArtPath = null)
    {
        if (_isDisposed || !_isInitialized) return;

        try
        {
            var summary = string.IsNullOrWhiteSpace(title) ? "Now Playing" : title;
            var body = string.IsNullOrWhiteSpace(artist) ? "" : artist;
            var icon = !string.IsNullOrEmpty(albumArtPath) && File.Exists(albumArtPath)
                ? albumArtPath
                : "audio-x-generic";

            await SendNotificationAsync(icon, summary, body, DefaultTimeout);
            _logger.LogDebug("Track notification: {Title} - {Artist}", title, artist);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show track notification");
        }
    }

    public async Task ShowConnectionStatusAsync(string serverName, bool connected)
    {
        if (_isDisposed || !_isInitialized) return;

        try
        {
            var summary = connected ? "Connected" : "Disconnected";
            var body = connected ? $"Connected to {serverName}" : $"Disconnected from {serverName}";
            var icon = connected ? "network-transmit-receive" : "network-offline";

            await SendNotificationAsync(icon, summary, body, 3000);
            _logger.LogDebug("Connection notification: {Status}", summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show connection notification");
        }
    }

    public Task CloseNotificationAsync(uint notificationId)
    {
        // notify-send doesn't support closing by ID
        return Task.CompletedTask;
    }

    private Task SendNotificationAsync(string icon, string summary, string body, int timeoutMs)
    {
        return Task.Run(() =>
        {
            try
            {
                var args = $"-a \"{AppName}\" -i \"{icon}\" -t {timeoutMs} \"{EscapeArg(summary)}\"";
                if (!string.IsNullOrEmpty(body))
                    args += $" \"{EscapeArg(body)}\"";

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "notify-send",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "notify-send failed");
            }
        });
    }

    private static string EscapeArg(string arg) =>
        arg.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public ValueTask DisposeAsync()
    {
        _isDisposed = true;
        return ValueTask.CompletedTask;
    }
}
