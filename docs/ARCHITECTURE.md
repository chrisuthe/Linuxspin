# Architecture, and the facts it rests on

Why this code is shaped the way it is, and — more usefully — the things about the SDK, the platforms
and the toolchain that were established **by measurement** rather than by reading documentation.

The second half is the important half. Most of the entries there contradict something plausible, and
several of them contradict what this project's own planning notes originally assumed. Any of them is
cheap to read here and expensive to rediscover.

## Layout

```
src/
├── Sendspin.Core/               net10.0  — contracts and platform-neutral logic
├── Sendspin.Platform.Shared/    net10.0  — session orchestration, audio base, artwork cache
├── Sendspin.Platform.Linux/     net10.0  — OpenAL + AL_SOFT latency, MPRIS2, portals
├── Sendspin.Platform.Windows/   net10.0-windows10.0.19041.0 — WASAPI, SMTC, taskbar transport
├── Sendspin.Platform.MacOS/     net10.0-macos — AUHAL, Now Playing, status item
├── Sendspin.Discord/            net10.0  — optional Rich Presence, off by default
├── Sendspin.Player/             Avalonia 12 UI, one head per platform TFM
└── Sendspin.Tests/              net10.0  — unit tests over Core and Platform.Shared
```

Protocol handling, the clock filter, the volume curve and the audio pipeline all belong to
`Sendspin.SDK`. This repository is the desktop player around it: the platform integrations the SDK
cannot provide, and the UI.

## Load-bearing structural decisions

**Decisions live in `Core`/`Platform.Shared`; platform projects are thin adapters.** Not primarily
for testability — for correctness. The duplicated-per-adapter version of this had already drifted
into a real bug, with Windows applying the loudness curve a second time on top of the SDK's. The
test project references only `Core` and `Platform.Shared`, and that constraint is a forcing function:
if a decision cannot be tested without referencing a platform project, the decision is in the wrong
assembly.

**Platform choice is a runtime decision.** Everything branches on `OperatingSystem.IsX()`. The single
exception is naming a concrete `IPlatformInitializer`, which needs a reference the other target
frameworks cannot have; that lives in one file per TFM under
`src/Sendspin.Player/PlatformSelection/`. Those files contain **wiring only** — a type name and a
backend call. Behaviour there is the same failure mode as a three-way `#if`: the compiler cannot tell
you when one of three parallel files falls behind. This is why the Wayland opt-in predicate lives in
`Core/Platform/WaylandOptIn.cs` with a test, rather than in the Linux file where it started.

**One media-session abstraction, shaped publish-state / receive-intent.** Not the union of SMTC,
MPRIS and `MPNowPlayingInfoCenter` — that union is three times the surface and every member leaks a
platform concept. Inbound intents route through `PlayerCommandRouter`, the same path a UI click
takes, so the server stays the authority on transport. A platform callback that touched the audio
pipeline directly would produce a player whose UI and whose group disagree.

**One audio pipeline and one clock synchroniser per process**, shared between the host and client
services. There is one output device. In `Auto` mode both services run at once, so giving each its
own pipeline put two audio players on one device and let a disconnect dispose the one the other was
still holding.

**Every background task is owned.** `BackgroundTaskSet` exists because `_ = Task.Run(...)` leaves a
task with no owner: nothing cancels it at shutdown, and a failure is swallowed as an unobserved
exception, so the work silently stops happening.

---

# Measured facts

Each of these was established by running something, not by reasoning about it.

## The SDK

### The volume curve is applied by the SDK, not by us

`AudioPipeline.SetVolume(int)` applies `(v/100)^1.5` **before** the value reaches
`IAudioPlayer.Volume`. Measured by driving the real pipeline through a stub player:

| Protocol volume | `IAudioPlayer.Volume` receives |
|---|---|
| 0 | 0.000000 |
| 25 | 0.125000 |
| 50 | 0.353553 |
| 75 | 0.649519 |
| 100 | 1.000000 |

So `Volume` is an **already-curved amplitude**, applied linearly. A platform player that raises it to
1.5 again halves the user's volume — 50 becomes 0.125 instead of 0.354. That bug was live on Windows,
and the "raw linear OpenAL gain" on Linux that looked wrong was correct.

`VolumeCurveTests.AudioPipeline_AppliesTheCurveExactlyOnce` pins this by asserting the SDK's output
against `VolumeCurve.ToAmplitude`, rather than asserting our arithmetic against itself. It fails if a
platform starts double-applying, **and** if a future SDK moves the curve out of the pipeline and
leaves every player applying none.

### `HighPrecisionTimer` is Unix-epoch; every device clock is boot-relative

`HighPrecisionTimer.GetCurrentTimeMicroseconds()` returns Unix-epoch microseconds — measured at
1785849872953039, matching `DateTimeOffset.UtcNow` to within 39 µs. Every platform's audio clock is
measured from boot: QPC on Windows, `mach_absolute_time` on macOS, `CLOCK_MONOTONIC` on Linux.

`GetAudioClockMicroseconds()` feeds the SDK's "now", which it compares against a server timestamp on
the epoch timeline. Returning a boot-relative value reports a sync error of about **fifty-six years**
and nothing ever plays.

`DeviceAnchoredClock` therefore takes its **origin** from the SDK's clock and only its **rate** from
the device frame counter. `AudioClockReading.HostTimeMicroseconds` stays on the platform's native
timebase and is deliberately *not* used in the projection — it is there for a backend's own latency
arithmetic and for spotting a discontinuity by comparing its delta against the frame delta.

The constant offset this anchoring costs is benign: the SDK self-measures and subtracts a residual
constant offset once its startup grace period ends. A wrong *rate* would never be absorbed.

### `ITimedAudioBuffer.Read` is obsolete; `ReadRaw` plus an external provider is the sanctioned path

`Read` carries `[Obsolete]`: *"Use ReadRaw() with external ISyncCorrectionProvider for correction
control."* This is why `SyncCorrectedSampleSource` exists and why it realises all three correction
modes itself.

**And it must account for every sample it takes.** `ReadRaw` advances the buffer's cursor past
everything it returns, so a reader that takes more than it plays and discards the surplus is
silently eating the stream. Two modes legitimately need to read further ahead than they emit — the
resampler needs a lookahead frame, and a drop consumes two input frames per output frame — so the
surplus is *retained* in a pending buffer, not dropped. Getting this wrong was measured at **49.5% of
the stream discarded** with a discontinuity in 99 of 100 buffers. `SyncCorrectedSampleSourceTests`
asserts the accounting per mode.

### `SyncCorrectionOptions` — shipped defaults, and why ours differ

| Option | SDK `Default` | SDK `CliDefaults` | Ours |
|---|---|---|---|
| `DeadbandMicroseconds` | 1000 | 1000 | **250** |
| `MaxSpeedCorrection` | 0.02 | 0.04 | **0.0005** |
| `CorrectionTargetSeconds` | 3.0 | 2.0 | 5.0 |
| `ResamplingThresholdMicroseconds` | 100 000 | 15 000 | **2500 (derived)** |
| `ReanchorThresholdMicroseconds` | 500 000 | 500 000 | 500 000 |

Two things matter here.

The SDK's 1000 µs deadband **is** the ±1 ms steady-state target, so leaving it would mean a 0.9 ms
error is simultaneously out of specification and ignored. Hence 250 µs.

The resampling threshold is **derived**, not chosen: at a 500 ppm ceiling over a 5 s target the
largest error rate adjustment can actually close is `500e-6 × 5 s = 2500 µs`. Handing the SDK a wider
threshold asks the resampler to close something it cannot reach, which is how a resampler ends up
permanently pinned at its bound while the error stays put. `SyncCorrectionPolicyTests` asserts the
derivation, and `Validate()` accepts the set.

### `waitForConvergence` does not mean what it looks like

On timeout the SDK logs `[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full
convergence.` and proceeds. There is no value meaning "never proceed unconverged", and
`SendspinClientService` owns the `client/state` message that reports availability. See
`docs/NEXT_STEPS.md` item 2.

### Other SDK API facts worth not rediscovering

- `IStaticDelayStore` is the SDK's persistence seam for `static_delay_ms`. It must be backed by the
  **same** config file as everything else; a store of its own is how a repo gets a second config file.
  A GroupSync calibration offset may be **negative** and must be round-tripped as given.
- `PlaybackProgress.TrackDuration` / `TrackProgress` are **milliseconds** on the wire.
  `TrackMetadata.Duration` / `Position` are the SDK's convenience properties and are **seconds**.
  Confusing them makes a four-minute track show as a quarter of a second — a test now pins the
  boundary.
- An **absent** duration is how the protocol expresses an unbounded (live) stream. It is meaningful,
  not missing data.
- The controller command is `switch`, not `switch_group`. Use `Commands.Switch`.
- `ClientCapabilities` has **no** `ArtworkFormats`/`ArtworkMaxSize`; it has
  `ArtworkChannels: List<ArtworkChannelSpec>`.
- The player role has **no seek command**. Position belongs to the server. `CanSeek` is therefore
  always false — advertising it gives every OS surface a scrubber that snaps back.
- `SyncCorrectionCalculator` **does** exist in 9.1.0. An earlier planning note said it did not.

## Platform audio

### Windows — `IAudioClock::GetPosition`, not `GetStreamLatency`

`GetStreamLatency` is documented as the *maximum* latency, is constant for the object's lifetime, is a
buffer-sizing hint, and **frequently returns 0** on Windows 10/11 (Mozilla's cubeb carries the comment
"This happens on windows 10: no error, but always 0 for latency"). `IAudioClock::GetPosition` instead
returns the stream position currently playing through the speakers paired with the QPC timestamp the
endpoint recorded it at — which is the "frame N hit the DAC at time T" anchor drift correction needs.

This requires holding the `AudioClient` yourself rather than using `WasapiOut`, whose `audioClient` is
private and whose `GetPosition()` discards the QPC value. Stay in **shared mode**: exclusive locks out
every other application, and `IAudioClient3` low-latency is a global engine setting.

`qpcPosition` is documented as already being in **100-nanosecond units**, so the conversion is `/10`
and is independent of `Stopwatch.Frequency`.

### macOS — AUHAL, and the selector trap

Measured on the build machine (macOS 26.6, Apple Silicon, 48 kHz):

- Mach timebase **125/3**, i.e. 24 MHz. Mach ticks are not nanoseconds; convert via
  `mach_timebase_info`.
- `mHostTime` arrives **11.646 ms ahead** of `mach_absolute_time`, against
  `(512 buffer + 48 safety) / 48000 = 11.667 ms`. That is direct evidence that buffer size and safety
  offset are **already inside `mHostTime`** — adding them again double-counts, which is cpal's bug.
- Built-in speakers: device latency **60 frames**, stream latency **690 frames**.
  `kAudioStreamPropertyLatency` and `kAudioDevicePropertyLatency` are **literally the same selector**
  `'ltnc'`; the difference is whether you query the device ID or a stream ID from
  `kAudioDevicePropertyStreams`. Querying the device twice gives 1.25 ms where the right answer is
  15.62 ms.
- Safety offset varies **48 → 576 frames** by transport, a 12× spread.
- There is **no `CoreAudio` namespace** in the Microsoft.macOS bindings, so the HAL property API is
  hand-rolled `[LibraryImport]`. `AudioComponent*`, `AudioUnit*`, `AudioStreamBasicDescription` and
  `AudioComponentDescription` *are* typed and are used from the bindings.

Reject `AVAudioEngine` (`presentationLatency` returns device latency only, under-reporting by
~14.4 ms) and `kAudioUnitProperty_Latency` (returns 0.0). AUHAL is not deprecated.

### Linux — Silk.NET cannot reach the latency extensions

`Silk.NET.OpenAL` does not bind the OpenAL Soft latency extensions **at all** — no `SourceLatency`,
no `DeviceClock`. They are hand-bound here via `alcGetProcAddress`/`alGetProcAddress` and
`delegate* unmanaged[Cdecl]`, in preference to OpenTK, which binds through
`Marshal.GetDelegateForFunctionPointer` and is not trim- or AOT-friendly.

Prefer `ALC_DEVICE_CLOCK_LATENCY_SOFT`, which returns clock and latency atomically.

**The OpenAL device clock is OpenAL's mixer clock, not `CLOCK_MONOTONIC`** — it jumps forward by
hundreds of samples per mix callback. So the call is sandwiched between two `clock_gettime` reads and
the pair is rejected if they disagree by too much (Shairport-Sync's double-read pattern).

Output latency is the measured device latency **plus the observed queue depth** from
`AL_BUFFERS_QUEUED`. The depth is read every pass rather than assumed to be `BufferCount - 1`,
because an assumed-full queue is wrong exactly when the number matters most: during prefill and after
an underrun.

`alcGetString`'s Silk.NET string overload truncates the double-NUL device list at the first entry,
which is why enumeration resolves the function pointer directly.

## Realtime audio, per platform

The three platforms genuinely differ, and the honest statement differs with them.

- **Windows and Linux have no OS-invoked audio callback at all.** The player owns a dedicated render
  thread. The requirement there is that the loop allocates nothing and takes no blocking lock, and
  that buffer depth is chosen so the deadline is tens of milliseconds. An owned managed thread is
  **not** GC-immune — a Gen2 pause stalls it exactly as it would stall a callback — so depth *is* the
  mitigation. This is what Snapcast does.
- **macOS AUHAL genuinely is callback-driven.** The callback is `[UnmanagedCallersOnly]` and does
  exactly two things: memcpy from an unmanaged SPSC ring, and publish `(frames, hostTime)` into a
  seqlock-protected unmanaged cell. **The residual GC transition remains** —
  `[UnmanagedCallersOnly]` removes marshalling but not the transition (dotnet/runtime#119142, closed
  as not planned). The ring and seqlock boundary is deliberately shaped so a native C shim can
  replace the callback without touching managed code. This is a labelled limitation, not a solved
  problem.

Ring and clock-cell memory is unmanaged (`NativeMemory.AlignedAlloc`), never a pinned managed array:
the consumer holds a raw pointer that must stay valid across collections.

**Reading the device clock is serialised against device teardown** in `AudioPlayerBase`. The SDK reads
it on every buffer read from its own thread, and each backend's reader dereferences something
teardown frees — an unmanaged cell, a COM object, a raw `ALCdevice*`. No backend can fix this alone,
because none of them is called for teardown except through the base class.

## Toolchain constraints

- **NAudio 3.0 has no stable release** (newest is `3.0.0-preview.19`). NAudio **2.2.1** already
  exposes the public `CoreAudioApi` `AudioClient` and `AudioClockClient.GetPosition(out pos, out qpc)`
  that the Windows plan needs, so nothing here wants a prerelease pin.
- **`Avalonia.Diagnostics` has no 12.x release** (11.3.18 is the last) — DevTools moved into the core
  package in Avalonia 12. Referencing it breaks restore.
- **Avalonia 12 renamed `TextBox.Watermark` to `PlaceholderText`** and `SystemDecorations` to
  `WindowDecorations`.
- **The macOS TFM is added to `TargetFrameworks` only when building on macOS.** The `macos` workload
  does not exist on other hosts, and restore evaluates every TFM regardless of which one you asked to
  build — so leaving it in unconditionally breaks the *Linux* build on a Linux runner.
- **`TargetPlatformVersion` must match between the macOS head and `Sendspin.Platform.MacOS`**, or the
  two are not reference-compatible and the error does not point at the cause. It is centralised as
  `SendspinMacOSPlatformVersion` in `Directory.Build.props`, and it is pinned because the SDK pack's
  Xcode validation demands an exact major.minor match against the installed Xcode. Override with
  `-p:SendspinMacOSPlatformVersion=<version>` when a runner's Xcode moves.
- **`OutputType` must be `Exe`, not `WinExe`, for the macOS head** — the SDK rejects `WinExe`
  outright, because the `.app` bundle decides windowed-versus-console rather than the PE subsystem.
- **`RuntimeIdentifiers` must be visible to restore** (which evaluates with `TargetFramework` empty)
  but **not** to the Windows head's inner build, where a single-entry list gets inferred as *the* RID
  and sends it looking for a `Microsoft.WindowsDesktop.App` runtime pack for `osx-arm64`. Hence the
  condition naming that one TFM.
- **A RID cannot be passed at the solution level** (NETSDK1134), and should not be: only the head
  being published needs one.
- `Makaretu.Dns.Multicast` → `Tmds.LibC` logs *"Could not find Tmds.LibC"* at startup in any
  `net10.0-macos` app, because `Tmds.LibC 0.2.0` ships reference assemblies only. Discovery still
  works — verified against two live servers — but it is worth recognising rather than chasing.

## Platform integration gotchas

Collected because each one is silent when wrong.

**MPRIS.** Both shells filter on the trailing dot, so the bus name must be
`org.mpris.MediaPlayer2.<app_id>`. `CanControl = true` (KDE gates every control on it),
`Properties.GetAll` must never error on either interface (KDE deletes the container if it does),
`PropertiesChanged` needs an **empty** `invalidated_properties` and the **full** `Metadata` map.
`xesam:artist` is `as` not `s`; `mpris:trackid` is an **object path** unique per track and outside the
reserved `/org/mpris` namespace; `mpris:length` is `x` in **microseconds**. Album art must be
`file://` — KDE's lock screen blocks `http` and `data:`, and GNOME has no `data:` backend — written to
a **unique filename per track**, because GNOME's texture cache is keyed on the icon string for the
life of the shell. Media keys need **nothing beyond MPRIS**: GNOME removed its `SettingsDaemon.MediaKeys`
API in 2021 with the message "superseded by MPRIS".

**Linux tray.** Avalonia's `SetTitleAndTooltip` early-outs on a null tooltip and ships a
mandatory-empty `Category`, so the icon **silently never appears** — always set `ToolTipText`. GNOME
needs the AppIndicator extension on every version in range; Avalonia's X11 fallback is a stub that
logs "not implemented" and is not even reached, because `IsActive` goes true as soon as a session bus
exists.

**Windows.** SMTC needs no package and no MSIX — `SystemMediaTransportControlsInterop` comes from the
TFM. `DisplayUpdater.Type = MediaPlaybackType.Music` is required; the timeline needs
`MinSeekTime`/`MaxSeekTime` or `PlaybackPositionChangeRequested` never fires; `ButtonPressed` arrives
on an MTA pool thread; throttle updates or the control becomes invisible-but-interactive. **Enabling
play and pause is functional, not polish** — without it Windows stops audio when the app backgrounds.
Windows 11 hides notification-area icons by default and Microsoft documents that this cannot be
controlled programmatically, so a taskbar overlay badge carries the always-visible affordance.
`AppNotificationManager.Register()` throws for **self-contained unpackaged** apps
(WindowsAppSDK#6071), which is why notifications go through `Shell_NotifyIcon` and why CI publishes
framework-dependent.

**macOS.** `NSApplication.Init()` **must be the first statement of `Main`**, before
`AppBuilder.Configure<App>()`. Avalonia bootstraps `NSApplication` through its own
`libAvaloniaNative.dylib`, so the macios runtime never records which thread is the UI thread, and
AppKit bindings with an `EnsureUIThread()` check then throw `AppKitThreadAccessException` on the
genuine main thread. It fails **selectively** — `MPNowPlayingInfoCenter` has no such check and works
either way — which makes it easy to misdiagnose. `playbackState` must be set on every playback
transition. Never run a 1 Hz elapsed-time timer: the system calculates elapsed time and frequent
metadata updates hit an undocumented throttle. Clear with `nil`, never a blank dictionary. `Console`
output is swallowed inside a macios-hosted `.app`; log to a file during bring-up.

**Flatpak.** Do **not** declare `--own-name=org.mpris.MediaPlayer2.*` — it is auto-granted for
`<app_id>.*` and declaring it is a Flathub linter error. Prefer `--filesystem=xdg-run/pipewire-0`
over `--socket=pulseaudio`, which forces `enable-shm=no` and pushes every buffer through the socket.
Album art must go to `$XDG_RUNTIME_DIR/app/$FLATPAK_ID/`, not `/tmp` — the sandbox's `/tmp` is not
the host's and the shell cannot follow a path into it.
