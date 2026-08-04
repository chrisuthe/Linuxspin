# Hosting `Sendspin.Platform.MacOS`

What the app head has to do for this backend to work. Everything here was either verified by
running it on macOS 26.6 / Xcode 26.6 / .NET SDK 10.0.302, or is called out below as unverified.

## 1. `NSApplication.Init()` must be the first statement of `Main`

```csharp
public static void Main(string[] args)
{
    AppKit.NSApplication.Init();          // must come first, before anything Avalonia
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
```

Avalonia bootstraps `NSApplication` itself through `libAvaloniaNative.dylib`, so the macios
runtime never records which thread is the UI thread. Bindings carrying an `EnsureUIThread()`
check then throw `AppKitThreadAccessException` **on the genuine main thread**.

It fails *selectively*, which is what makes it easy to misdiagnose: `MPNowPlayingInfoCenter` has
no such check and works either way, so Now Playing can be fine while the status item throws.
`NSStatusBar` is the binding in this project most likely to trip it.

## 2. Project properties on the macOS head

The workload's default SDK pack for a bare `net10.0-macos` is **26.0**, whose
`_RecommendedXcodeVersion` is 26.0, and the pack's `_ValidateXcodeVersion` target demands an
*exact* major.minor match against the installed Xcode. With Xcode 26.6 that fails outright. Pin
the pack:

```xml
<TargetFramework>net10.0-macos</TargetFramework>
<!-- Single source of truth: SendspinMacOSPlatformVersion in Directory.Build.props, 26.5,
     which recommends Xcode 26.6. Override per machine or CI runner with
     -p:SendspinMacOSPlatformVersion=<version>. -->
<TargetPlatformVersion>$(SendspinMacOSPlatformVersion)</TargetPlatformVersion>
<SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
<ApplicationId>…</ApplicationId>                      <!-- required even for a library -->
```

`_ValidateXcodeVersion` only runs when the project can emit an app bundle, so this bites the app
head harder than it bites this library. **The CI runner's Xcode version and
`SendspinMacOSPlatformVersion` have to agree**, and the check is exact — a runner image bump to
Xcode 27.x needs the property moved with it.

Also from the brief, and not re-verified here: NativeAOT is broken on Avalonia + macios
(Avalonia#17425), ship arm64-only, and do **not** set `EnableCompressionInSingleFile` (bus
errors on Apple Silicon).

## 3. `Info.plist` keys

| Key | Why |
|---|---|
| `NSLocalNetworkUsageDescription` | macOS 15+ gates Bonjour, `*.local` resolution **and local-subnet TCP** behind a user grant. That is everything discovery and streaming do, so without this the app is silently unable to reach a server. |
| `NSBonjourServices` | Array of the service types browsed, e.g. `_sendspin._tcp`. Unlisted types are not resolvable, with no error that says so. |
| `CFBundleIdentifier` | `UserNotificationService` refuses to initialise without a bundle identifier, by design — see §5. |
| `LSMinimumSystemVersion` | Match `SupportedOSPlatformVersion`. |
| `NSHighResolutionCapable` | `true`. |

`LSUIElement` is a product decision, not a requirement: setting it gives a menu-bar-only app with
no Dock icon, which suits a background endpoint but removes the Dock as a way back to the window.
`IStatusItemPresenter` works either way.

**TCC keys the local-network grant to the code signature**, so ad-hoc signing means a new hash on
every rebuild and a grant that quietly stops applying. There is no way to reset an app's
local-network grant. Use a real Developer ID certificate even in development, and test by
launching the `.app` from Finder — running the binary from a terminal is auto-allowed and gives a
false pass.

## 4. Service wiring

`MacPlatformInitializer.RegisterServices` registers:

| Contract | Implementation | Lifetime |
|---|---|---|
| `IPlatformPaths` | `MacPaths` | singleton |
| `IAudioDeviceEnumerator` | `CoreAudioDeviceEnumerator` | singleton |
| `Sendspin.SDK.Audio.IAudioPlayer` | `AuhalRenderPlayer` | **transient** |
| `IMediaSession` | `NowPlayingMediaSession` | singleton |
| `INotificationService` | `UserNotificationService` | singleton |
| `IStatusItemPresenter` | `StatusItemPresenter` | singleton |

`IAudioPlayer` is transient because the SDK's `AudioPipeline` takes a `Func<IAudioPlayer>` and
builds one per stream, disposing the previous. Resolve it through that factory, never as a
singleton — a disposed player has an uninitialised audio unit and a freed ring.

`MacPlatformInitializer.InitializeAsync` deliberately does nothing. The two services that need
starting have their own `InitializeAsync`, and both want the UI thread:

```csharp
await notifications.InitializeAsync(ct);   // may put a permission dialog on screen
await mediaSession.InitializeAsync(ct);
statusItem.Show();
```

Then wire both intent sources into the same command router, and publish state to both surfaces:

```csharp
mediaSession.IntentReceived += OnIntent;
statusItem.IntentReceived  += OnIntent;
// on every state change:
mediaSession.Publish(state);
statusItem.Update(state);
```

`IStatusItemPresenter` lives in `Sendspin.Platform.MacOS.MediaSession`, not in Core — a status
item has no cross-platform analogue worth abstracting. Resolve it only on macOS.

Dispose `IMediaSession` and `IStatusItemPresenter` before the process exits, or the Now Playing
entry and the menu bar icon outlive the app.

`AuhalRenderPlayer` inherits `ManualLatencyOffsetMs` from `AudioPlayerBase`. The app must apply
the user's persisted per-device offset after creating each player; the analog, Bluetooth and
AirPlay tails are unmeasurable and that offset is the only route to a correct figure for them.

## 5. Notifications need a bundle, and the launch mechanism decides

`UserNotificationService.InitializeAsync` checks `NSBundle.MainBundle.BundleIdentifier` first and
reports `IsAvailable = false` with a logged reason rather than throwing. Without that check,
touching `UNUserNotificationCenter.Current` from a bare executable raises
`bundleProxyForCurrentProcess is nil`.

`dotnet run` will fail this way. So will `./Sendspin.app/Contents/MacOS/Sendspin` — the
discriminator is the launch mechanism, not the file layout. Launched through LaunchServices
(`open Sendspin.app`, or Finder) the app gets a real permission dialog; exec'd directly,
authorisation returns "Notifications are not allowed" with the bundle sitting right there.

The bundle does **not** need notarizing or a real identity for notifications — ad-hoc is enough.
That is only true of notifications; the local-network grant in §3 does need a stable signature.

Ensure `Contents/MacOS/<binary>` is a **real file, not a symlink** — check that against whatever
`PublishSingleFile` produces.

## 6. `Console` is swallowed inside the `.app`

Once the process is hosted by macios, `Console.WriteLine` goes nowhere. Every diagnostic in this
backend — which device was opened, the measured latency and its terms, why notifications are off —
is written through `ILogger`. **Configure a file sink under
`IPlatformPaths.LogDirectory` (`~/Library/Logs/Sendspin`) before initialising these services**, or
bring-up is blind. `~/Library/Logs` is also where Console.app looks, which is why `MacPaths`
overrides it.

## 7. Artwork

`NowPlayingMediaSession` and `UserNotificationService` both take artwork as
`MediaSessionState.ArtworkFilePath` / `NotificationRequest.ArtworkFilePath` — a file path, never
bytes. The app must run incoming artwork through `Sendspin.Platform.Shared.Media.ArtworkCache` and
put the resulting path on the state it publishes, or Now Playing shows no album art.

## 8. One thing found while testing that is not this backend's to fix

Running any `net10.0-macos` app that references `Sendspin.SDK` prints at startup:

```
Could not find `Tmds.LibC, Version=0.2.0.0` referenced by assembly `Makaretu.Dns.Multicast, Version=0.27.0.0`.
```

`Tmds.LibC 0.2.0` ships **reference assemblies only** (`ref/netstandard2.0`, empty `runtime`), so
it never reaches the output directory. On plain `net10.0` nothing notices, because the CLR resolves
lazily; macios registers referenced assemblies eagerly at startup, which is why it surfaces here.
The app ran fine in testing, so it is a warning rather than a failure — but `Makaretu.Dns.Multicast`
is the SDK's mDNS implementation, so **service discovery on macOS should be tested explicitly**,
and if it throws, this is the first place to look. Not fixable from this project.

## 9. What could not be verified here

Verified by running on this machine: device enumeration and its UIDs, the latency query against
built-in speakers (device 60 + stream 690 frames) and an external display (88 + 0), the AUHAL
render callback, the audio clock's rate, survival of three forced compacting Gen2 collections
mid-playback, device switching in both directions, and pause/resume/stop/dispose.

**Not verified, and it needs a signed `.app` launched from Finder:** the Now Playing panel and
Control Center actually showing metadata and responding to media keys, notifications being
delivered, the status item appearing in a live Avalonia `NSApplication`, and local-network
discovery under the macOS 15+ TCC prompt. All four depend on bundling, signing or an AppKit event
loop that a library test host does not have.
