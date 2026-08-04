# Spec compliance posture

What this client implements, what it does not, and — importantly — **which servers it can
actually talk to**. Nothing here is aspirational: if a row says "not implemented", the code does
not implement it.

Last reviewed against `Sendspin/spec` HEAD of 2026-07-31 and `Sendspin.SDK` **9.1.0**.

## Summary

| Role | Status |
|---|---|
| `player@v1` | Implemented |
| `controller@v1` | Implemented (transport commands, volume, mute, `switch`) |
| `metadata@v1` | Implemented (title, artist, album, album artist, progress) |
| `artwork@v1` | Implemented (one album channel, JPEG, 512×512) |
| `visualizer@v1` | **Not implemented** — not advertised, so no visualizer stream is negotiated |
| `color@v1` | **Not implemented** — the SDK surfaces `ColorChanged`; the UI does not consume it yet |
| `management@v1` | **Not implemented** — see "Blocked on the SDK" below |
| Noise `KKpsk2` transport encryption | **Not implemented** — see below |
| Pairing (PSK, and the optional CPace PIN flow) | **Not implemented** — see below |

## Which servers this build can talk to

**Plain, unencrypted `ws://` servers only.**

The spec mandates Noise `KKpsk2` over `ws://` with the server as initiator, requires clients to
implement Pairing PSK, and requires the management role of all clients. `Sendspin.SDK` 9.1.0 ships
none of the three. That work is merged on `sendspin-dotnet` `main` after 9.1.0 (PRs #67 and #73)
but is not in a published package, and **this repository deliberately does not hand-roll it**:
Noise and CPace are cross-client protocol concerns that belong in the SDK, and a second
implementation of a key exchange is a liability rather than an asset.

The practical consequence, stated plainly rather than discovered at runtime:

- Against a server that **requires** encryption, this build **will not connect at all**.
- Against a server that still accepts plain `ws://`, it connects and plays.

Releases built on SDK 9.1.0 must therefore be labelled **pre-encryption**. Do not describe them as
spec compliant.

**To lift this:** bump the `Sendspin.SDK` pin in `Directory.Packages.props` to the first published
10.x, then add pairing UI and the management role. No other change here anticipates it, and none
should until the API is real.

## Player-role behaviour that is ours

These are not blocked on anything and are implemented here.

| Requirement | Where |
|---|---|
| `amplitude = (volume/100)^1.5`, applied exactly once | The SDK's `AudioPipeline.SetVolume` applies it; platform players treat `IAudioPlayer.Volume` as an already-curved amplitude and multiply linearly. Pinned by `VolumeCurveTests.AudioPipeline_AppliesTheCurveExactlyOnce`. |
| `static_delay_ms` reported, adjustable, and persisted across restarts | `SettingsStaticDelayStore`, backed by the one settings file. Covered by `SettingsPersistenceTests`. |
| `required_lead_time_ms` and `min_buffer_ms` reported | `PlayerCapabilities` |
| Stable, persisted, platform-neutral `client_id` | `ClientIdentity` |
| Soft-sync bounded well inside ±0.5 % speed deviation | `SyncCorrectionPolicy` caps rate correction at ±500 ppm (0.05 %) and derives the SDK's resampling threshold from that cap |
| Late chunks dropped rather than played | SDK `TimedAudioBuffer` |
| No `Thread.Sleep` on an audio or playback thread | Both thread-based backends wait on an event with a bounded timeout |
| Controller command is `switch`, not `switch_group` | Uses the SDK's `Commands.Switch` constant |

## Known gaps, with reasons

### Availability is not withheld until the clock filter converges

**Status: cannot be fixed in this repository against SDK 9.1.0.**

The spec requires a player to withhold availability until its time filter has converged, with no
"timed out, proceed anyway" path. The pipeline is constructed with `waitForConvergence: true`, but
SDK 9.1.0 does not honour that absolutely — on timeout it logs

```
[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full convergence.
```

and proceeds, and `SendspinClientService` owns the `client/state` message that reports
availability. There is no parameter meaning "never proceed unconverged".

The timeout is left at the SDK default of 5000 ms **on purpose**. Inflating it would hide the
behaviour without fixing it, and would trade a known bounded gap for an unbounded startup stall.

**Action required upstream:** file this against `sendspin-dotnet` and record the issue number
here. Until then this row stays a declared gap, not a ticked box.

### Realtime-audio safety differs by platform, and one platform is knowingly short

- **Windows and Linux** have no OS-invoked audio callback at all: the player owns a dedicated
  render thread. The render loop allocates nothing per iteration and takes no blocking lock. An
  owned managed thread is *not* GC-immune — a Gen2 pause stalls it exactly as it would stall a
  callback — so the mitigation is buffer depth, chosen so the deadline is tens of milliseconds
  rather than the ~10.7 ms a 512-frame callback at 48 kHz would give. This is the same approach
  Snapcast takes.
- **macOS AUHAL** genuinely is callback-driven. The callback is `[UnmanagedCallersOnly]` and does
  only a memcpy from an unmanaged ring plus a seqlock timestamp publish. **The residual GC
  transition remains**: `[UnmanagedCallersOnly]` removes marshalling but not the transition
  (dotnet/runtime#119142, closed as not planned). The ring and seqlock boundary is deliberately
  shaped so a native C shim can replace the callback without touching managed code, which is the
  fix. This is a labelled limitation, not a solved problem.

### Not verified on hardware

The implementation of each of these has landed; the verification has not, because it needs a
machine this was not built on. They are listed so the gap is visible rather than implied.

- Steady-state sync error within ±1 ms, and speed deviation within ±0.5 % over a 150 ms window,
  measured against a second client on a real server, on each of Windows, macOS and Linux.
- Corrections being inaudible, verified by listening.
- The Windows 11 media flyout and hardware media keys.
- MPRIS under Plasma 6 and GNOME, including `busctl --user introspect` output and album art
  loading from `file://`.
- Tray behaviour on GNOME with the AppIndicator extension, including its bus name vanishing on a
  shell restart.
- Now Playing / Control Center on macOS.
- Fractional HiDPI at 125 % and 150 %.
- MPRIS and the tray working *inside* the Flatpak rather than only outside it.

### Blocked on a Developer ID certificate (macOS)

CI produces an unsigned `.app` and dmg. Signing, notarization and stapling need a Developer ID
certificate, and **ad-hoc signing is not a partial substitute**: TCC keys the local-network grant
to the code signature, so an ad-hoc hash that changes on every rebuild yields a grant that
silently stops applying. That produces *false* discovery results rather than merely unsigned
builds, which is worse than not signing at all.

When a certificate exists:

- Entitlements must contain `com.apple.security.cs.allow-jit` **and nothing else**.
  `allow-unsigned-executable-memory` was removed from .NET in 2021, and
  `disable-library-validation` is avoidable by re-signing Microsoft's `libSkiaSharp` and
  `libHarfBuzzSharp` dylibs with the project identity.
- Sign inside-out, never `--deep` (Apple: "Do not use the `--deep` argument").
- Archive with `ditto -c -k --sequesterRsrc --keepParent`, not `zip`.
- Notarize with `notarytool`, and staple both the `.app` and the dmg.
- Test local-network discovery by launching the `.app` **from Finder**. Running the binary from a
  terminal is auto-allowed and gives a false pass, and there is no way to reset an app's
  local-network grant — so test on a snapshot.

### Conformance harness

The `Sendspin/conformance` harness is **not wired into CI**. It is a separate repository and its
adapter and invocation contract were not available to this change, so wiring it would have meant
guessing at an interface. Named here rather than quietly dropped; the CI job is the remaining
work, and the unit suite is not a substitute for it.

## Notifications on Windows

`Microsoft.Toolkit.Uwp.Notifications` is gone: the repository was archived in 2022 and its
successor is formally deprecated on NuGet.

`AppNotificationManager` is **not** used either, because `Register()` throws for **self-contained
unpackaged** apps (WindowsAppSDK#6071). Windows notifications therefore go through
`Shell_NotifyIcon` balloon notifications, which need no package and no package identity and work
unpackaged. The route in use is logged at startup so it is visible in a bug report rather than
inferred. The Windows CI job publishes framework-dependent for the same family of reasons.
