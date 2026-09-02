# Spec compliance posture

What this client implements, what it does not, and — importantly — **which servers it can
actually talk to**. Nothing here is aspirational: if a row says "not implemented", the code does
not implement it.

Last reviewed against `Sendspin/spec` HEAD of 2026-07-31 and `Sendspin.SDK` **9.3.2**.

Companion documents: `docs/NEXT_STEPS.md` is what remains and who it needs;
`docs/ARCHITECTURE.md` records the measured facts this code rests on, including several that
contradict what the plan originally assumed.

## Summary

| Role | Status |
|---|---|
| `player@v1` | Implemented |
| `controller@v1` | Implemented (transport commands, volume, mute, `switch`) |
| `metadata@v1` | Implemented (title, artist, album, album artist, progress) |
| `artwork@v1` | Implemented (one album channel, JPEG, 512×512) |
| `visualizer@v1` | Implemented (loudness and beat at up to 30 frames a second, 4 096-byte buffer; no spectrum) — drives the living backdrop |
| `color@v1` | Implemented (the server's palette, picked per theme variant) — drives the living backdrop |
| `management@v1` | **Not implemented** — see "Blocked on the SDK" below |
| Noise `KKpsk2` transport encryption | **Not implemented** — see below |
| Pairing (PSK, and the optional CPace PIN flow) | **Not implemented** — see below |

## Which servers this build can talk to

**Plain, unencrypted `ws://` servers only.**

The spec mandates Noise `KKpsk2` over `ws://` with the server as initiator, requires clients to
implement Pairing PSK, and requires the management role of all clients. `Sendspin.SDK` 9.3.2 ships
none of the three: 9.3.2 is the **9.x legacy line**, a curated backport for apps that cannot take
10.0's breaking renames, and encryption is not part of it. That work is merged on `sendspin-dotnet`
`main` (PRs #67 and #73) but **no 10.x is published on nuget.org at all — not even a prerelease**,
so "blocked on 10.x" means blocked on something nobody can resolve today. And **this repository
deliberately does not hand-roll it**: Noise and CPace are cross-client protocol concerns that belong
in the SDK, and a second implementation of a key exchange is a liability rather than an asset.

The practical consequence, stated plainly rather than discovered at runtime:

- Against a server that **requires** encryption, this build **will not connect at all**.
- Against a server that still accepts plain `ws://`, it connects and plays.

Releases built on SDK 9.3.2 must therefore be labelled **pre-encryption**. Do not describe them as
spec compliant.

**To lift this:** bump the `Sendspin.SDK` pin in `Directory.Packages.props` to the first published
10.x — which does not exist yet — then add pairing UI and the management role. No other change here
anticipates it, and none should until the API is real.

## Player-role behaviour that is ours

These are not blocked on anything and are implemented here.

| Requirement | Where |
|---|---|
| `amplitude = (volume/100)^1.5`, applied exactly once | The SDK's `AudioPipeline.SetVolume` applies it; platform players treat `IAudioPlayer.Volume` as an already-curved amplitude and multiply linearly. Pinned by `VolumeCurveTests.AudioPipeline_AppliesTheCurveExactlyOnce`. |
| `static_delay_ms` reported, adjustable, and persisted across restarts | `SettingsStaticDelayStore`, backed by the one settings file. Covered by `SettingsPersistenceTests`. |
| `required_lead_time_ms` and `min_buffer_ms` reported | `PlayerCapabilities` |
| Stable, persisted, platform-neutral `client_id` | `ClientIdentity` |
| Soft-sync bounded well inside ±0.5 % speed deviation | `SyncCorrectionPolicy` caps rate correction at ±500 ppm (0.05 %) and derives the SDK's resampling threshold from that cap. 9.3.0 made ±0.5 % a cap the SDK itself enforces; 500 ppm is a tenth of it, so nothing is clamped and no over-cap warning is raised — asserted by `SyncCorrectionPolicyTests.ToSdkOptions_StaysInsideTheSpecSpeedCapWithoutBeingClamped` |
| Exactly one connection method at a time (connection.md) | `SendspinPlayerService.StartAsync` starts discovery **or** advertising, never both. `ConnectionMode.Auto` is no longer offered, and a persisted `Auto` migrates to `AdvertiseOnly` |
| `buffer_capacity` advertised as a figure the buffer can honour | `PlayerCapabilities.Build` leaves it unset so the SDK derives it from the decoded buffer's 30 s default and the advertised formats. Pinned by `PlayerCapabilitiesTests` |
| Late chunks dropped rather than played | SDK `TimedAudioBuffer` |
| No `Thread.Sleep` on an audio or playback thread | Both thread-based backends wait on an event with a bounded timeout |
| Controller command is `switch`, not `switch_group` | Uses the SDK's `Commands.Switch` constant |

## Known gaps, with reasons

### Availability is not withheld until the clock filter converges

**Status: cannot be fixed in this repository against SDK 9.3.2.**

**Re-verified against 9.3.2, not carried forward.** 9.3.0 reworked the clock filter's probe cadence
— a link that stays noisy now falls back to the steady-state interval and withholds `IsClockSynced`
— so the wording below was checked against the shipped assembly rather than assumed still true. The
timeout path is unchanged: the log string below is byte-identical in 9.1.0 and 9.3.2.

The spec requires a player to withhold availability until its time filter has converged, with no
"timed out, proceed anyway" path. The pipeline is constructed with `waitForConvergence: true`, but
SDK 9.3.2 does not honour that absolutely — on timeout it logs

```
[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full convergence.
```

and proceeds, and `SendspinClientService` owns the `client/state` message that reports
availability. There is no parameter meaning "never proceed unconverged".

The timeout is left at the SDK default of 5000 ms **on purpose**. Inflating it would hide the
behaviour without fixing it, and would trade a known bounded gap for an unbounded startup stall.

**Action required upstream, and not yet done.** No issue has been filed against
`sendspin-dotnet` — `AI_POLICY.md` bars an agent from opening issues, so this needs a human. The
issue number belongs in this paragraph once it exists. Until then the row is a declared gap, not a
ticked box, and no build should describe itself as meeting the convergence-gating requirement.

### `supported_roles` must carry the `@v1` suffix, and SDK 9.3.2 does not add it

**Found by running the Linux build against Music Assistant; it silenced every platform, not just
Linux.**

`ClientRoles.Player` and its siblings are the pre-versioning spellings — bare `player`,
`controller`, `metadata`, `artwork`. A current server matches `supported_roles` against **versioned**
identifiers, and SDK 9.3.2 is half-migrated: it already emits its support objects keyed
`player@v1_support` and `artwork@v1_support`, and only the role list kept the old spellings. A server
therefore sees support objects for roles the client never listed:

```
non-compliant client: client/hello sent support objects for unlisted roles: player@v1, artwork@v1
Client offered roles/versions this server does not implement: ['player', 'controller', 'metadata', 'artwork']
```

It then activates nothing — `server/hello` comes back with `active_roles: []` — registers the client
as a *protocol* entry rather than a player, and never sends `stream/start`. The client connects,
clock-syncs, reports `synchronized` and looks entirely healthy while no audio is ever requested of
it. **Silence with no error on the client side is the signature of this bug**, and it is worth
recognising because nothing in a client-side log points at it.

`PlayerCapabilities.AdvertisedRoles` now advertises `player@v1`, `controller@v1`, `metadata@v1` and
`artwork@v1`. Verified live: `active_roles` returns all four, `stream/start` arrives, and audio,
artwork and metadata all work. Pinned by
`PlayerCapabilitiesTests.Roles_AreAdvertisedWithTheirVersionSuffix` and
`Roles_CoverEveryRoleTheSdkEmitsASupportObjectFor`, both of which fail against the bare spellings.

This is ours to fix rather than a blocked-on-10.x item, because `ClientCapabilities.Roles` is a
plain settable `List<string>` — the SDK imposes no constant. If a 10.x ever ships versioned members
on `ClientRoles`, this list should be built from those instead of interpolating the suffix.

### `color@v1` and `visualizer@v1` — the living backdrop's two roles

Both advertised since reskin phase 5, and both consumed: the palette recolours the Ambient Glow
blobs and the Breathing Art glow, the loudness frames drive their energy and the beat frames their
pulse. `visualizer@v1` goes with a `visualizer@v1_support` object asking for `loudness` and `beat`
at up to 30 frames a second with a 4 096-byte buffer, and no spectrum — the SDK emits the support
object whenever `ClientCapabilities.VisualizerSupport` is set, so the role and the object have to
travel together or the hello is the non-compliant one quoted above. Pinned by
`PlayerCapabilitiesTests.Roles_AreAdvertisedWithTheirVersionSuffix`,
`Roles_CoverEveryRoleTheSdkEmitsASupportObjectFor` and `VisualizerSupport_AsksForLoudnessAndBeatOnly`
against the capabilities object, and by
`ClientAdvertisementTests.Visualizer_SupportIsAdvertisedAlongsideTheVisualizerAndColorRoles` on the
wire. What the player does with the frames is the UI's business and is recorded under "As shipped
(reskin phase 5)" in `docs/ARCHITECTURE.md`. Spectrum, peak and pitch are not requested.

### Two defects the 9.3.2 bump closed

Recorded because both were real and neither is visible in the diff as a fix.

- **`buffer_capacity` was advertising about one second of audio.** `PlayerCapabilities` set it to
  `8_000` from a constant named and commented as milliseconds; the field is **compressed bytes**.
  The spec makes it a hard per-player byte limit that servers fill toward, so the server was doing
  exactly as told and the player was simply starved. Leaving it unset lets the SDK derive it — with
  this build's format list, 192 000 bytes, which is 4/5 of what 30 s of the thinnest advertised
  format occupies. The thinnest is a bitrate-less Opus entry, valued at the SDK's conservative
  64 kbps fallback; declaring `AudioFormat.Bitrate` would tighten it and is deliberately not done.
  The decoded PCM ring grows from 8 s to 30 s with it — roughly 3 MB to 11 MB at 48 kHz stereo — an
  accepted cost.
- **The reported sync error was converging at twice the physical correction.** Up to 9.2,
  `NotifyExternalCorrection` moved the read cursor that `SyncErrorMicroseconds` is measured against,
  while `SyncCorrectedSampleSource` already sizes its reads to the correction — so the same frames
  were counted twice and the player read near zero while sitting about half the drift out of the
  group. 9.3.0 made the call stats-only, which fixes it here without a code change. Nothing in this
  repository was wrong; the number it was displaying was.

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

### `required_lead_time_ms` is a constant, not derived from the pipeline

The acceptance criterion asks for it to be derived from the actual pipeline. It is 350 ms fixed. The
value has to be advertised in `client/hello`, which is sent before any audio has flowed and so
before the pipeline can report a measured latency; deriving it properly means re-advertising after
the first stream, which the SDK's `ClientCapabilities` is not shaped for. Declared rather than
quietly left looking derived.

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

### macOS audio measurements that *were* taken

Unlike the rows above, these were measured on the build machine (macOS 26.6, Apple Silicon,
48 kHz) rather than carried over from research, and the code's comments cite them:

- Mach timebase 125/3, i.e. 24 MHz — mach ticks are not nanoseconds.
- `mHostTime` arrives 11.646 ms ahead of `mach_absolute_time`, against
  `(512 buffer + 48 safety) / 48000 = 11.667 ms`. That is direct evidence that buffer size and
  safety offset are **already inside** `mHostTime`, so adding them again double-counts.
- Built-in speakers: device latency 60 frames, **stream latency 690 frames**. Querying the device
  twice instead of querying the stream gives 1.25 ms where the right answer is 15.62 ms.
- Safety offset varies 48 → 576 frames by transport, the 12× spread the plan predicted.
- A player run reported `latency=16ms`, `calibrated=40ms`, and `TimingSourceName` moving
  `wall-clock` → `audio-clock` on the first publish, with the clock advancing 202666 µs per 200 ms
  of wall time — exactly 19 × 512-frame buffers, so the rate is the DAC's.
- Three forced compacting Gen2 collections mid-playback did not stop the callback or the clock.

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

## How the claims above were checked

Reproducible on any machine with the .NET 10 SDK:

```bash
dotnet build Sendspin.Player.slnx -c Release      # all heads, warnings are errors
dotnet test  src/Sendspin.Tests/Sendspin.Tests.csproj -c Release
dotnet list  src/Sendspin.Tests/Sendspin.Tests.csproj package --vulnerable --include-transitive
```

At the time of writing: 143 tests pass and no project reports a vulnerable package. The Linux,
Windows and shared heads build clean with zero warnings on a Linux machine; the macOS head needs
the `macos` workload and so is built only on the macOS CI runner.

The suite was confirmed to be a real gate rather than decoration by changing
`VolumeCurve.Exponent` from 1.5 to 1.4 — six tests failed, including the one that drives the SDK's
own pipeline — and passing again on restore. Do that again if you ever doubt it.

Mechanical checks the acceptance criteria name, all currently returning nothing:

```bash
grep -rn '#if WINDOWS' src/
grep -rnE 'catch\s*\{' src/
grep -rn 'Thread\.Sleep' src/
grep -rnE 'Math\.Pow|MathF\.Pow' src/Sendspin.Platform.*/
grep -rn 'Version="[0-9]*\.\*"' src/ Directory.Packages.props
grep -c 'continue-on-error:' .github/workflows/build.yml
```

**What has actually been run, end to end:** the macOS `.app` launches, mints and persists its client
identity, creates the tray item, registers with `MPRemoteCommandCenter`, and found two live Music
Assistant servers on a real network. That run was made in the since-retired `Auto` mode, which
advertised and discovered simultaneously; the discovery half of it is what `DiscoverOnly` now does
on its own. The audio *path* was exercised on macOS (see the measurements above). Nothing has been
verified on Windows or Linux, and no synchronised playback against a second client has been measured
anywhere; see `docs/NEXT_STEPS.md` item 4.
