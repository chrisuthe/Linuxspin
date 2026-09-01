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
services. There is one output device. The two services coexist even though only one *connection
method* runs: the manual-connect path and the auto-connect policy both dial out while the host is
advertising. Giving each its own pipeline put two audio players on one device and let a disconnect
dispose the one the other was still holding. That coexistence is also why the host has to be told
about a session this player dialled — see the arbitration note below.

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

Measured against 9.3.2 by reflection, because 9.3.0 moved two of these defaults and added a third
row:

| Option | SDK `Default` (9.1.0) | SDK `Default` (9.3.2) | SDK `CliDefaults` | Ours |
|---|---|---|---|---|
| `DeadbandMicroseconds` | 1000 | **100** | 100 | **250** |
| `MaxSpeedCorrection` | 0.02 | **0.005** | 0.005 | **0.0005** |
| `CorrectionTargetSeconds` | 3.0 | 3.0 | 2.0 | 5.0 |
| `ResamplingThresholdMicroseconds` | 100 000 | 100 000 | 15 000 | **2500 (derived)** |
| `HardSyncThresholdMicroseconds` | — | 5000 | 5000 | 5000 (not settable) |
| `ReanchorThresholdMicroseconds` | 500 000 | 500 000 | 500 000 | 500 000 |

Three things matter here.

**The deadband is now a widening, not a tightening.** Against 9.1.0's 1000 µs — which *was* the
±1 ms steady-state target, so a 0.9 ms error was simultaneously out of specification and ignored —
250 µs was tighter. 9.3.0 dropped the default to 100 µs, so 250 µs is now the looser choice, kept
because a desktop player sharing a machine with everything else does not settle at 100 µs, it hunts.

**The resampling threshold is derived, not chosen:** at a 500 ppm ceiling over a 5 s target the
largest error rate adjustment can actually close is `500e-6 × 5 s = 2500 µs`. Handing the SDK a wider
threshold asks the resampler to close something it cannot reach, which is how a resampler ends up
permanently pinned at its bound while the error stays put. `SyncCorrectionPolicyTests` asserts the
derivation, and `Validate()` accepts the set.

**The ceiling is now enforced, not merely advised.** 9.3.0 clamps `MaxSpeedCorrection` to the spec's
0.005 where correction is applied and logs a warning once for anything above it. 500 ppm is a tenth
of the cap, so nothing here is clamped and no warning is raised — asserted rather than assumed, by
reading the SDK's internal `ExceedsSpecSpeedCap` in
`SyncCorrectionPolicyTests.ToSdkOptions_StaysInsideTheSpecSpeedCapWithoutBeingClamped`.

### The correction ladder, as it actually is on 9.3.2

9.3.0 inserted a one-shot **hard sync** tier between drop/insert and re-anchor. With this repo's
thresholds the full ladder is:

| Smoothed error | Band | What the SDK does |
|---|---|---|
| ≤ 250 µs | deadband | nothing — correcting here chases measurement noise |
| ≤ 2500 µs | rate adjust | continuous resampling, bounded by 500 ppm |
| ≤ 5 ms | drop/insert | discrete frame splicing |
| < 500 ms | hard sync | snaps the whole error in one step, exempt from the ±0.5 % cap |
| ≥ 500 ms | re-anchor | clears the buffer and restarts sync |

`SyncCorrectionBand` mirrors this so the diagnostics view can name the tier rather than mislabel a
snap as drop/insert; `SyncCorrectionPolicyTests.Classify_CoversTheLadderInOrder` pins all four
boundaries.

**This player is one of the few that reaches the drop/insert band at all.** The SDK's docs say the
band is skipped with its shipped settings — hard sync takes precedence above 5 ms, which is below the
default 100 ms resampling threshold — and is used only when a caller lowers that threshold below the
hard-sync one. The 500 ppm ceiling is exactly what forces that.

`HardSyncThresholdMicroseconds` is **internal** on the 9.x line, so 5 ms is the SDK's figure and
cannot be changed. `SyncCorrectionPolicy` restates it only so `Classify` knows where the tier
begins; the value does not travel back through `ToSdkOptions`.

### `buffer_capacity` is compressed bytes, and is best left to the SDK

Measured against 9.3.2: `ClientCapabilities.BufferCapacity` is an `int` of **compressed bytes**, not
milliseconds. Unset, the SDK derives it from `TimedAudioBuffer`'s 30 s decoded default and the
advertised formats, taking `(N-1)/N` of what the *thinnest* format occupies — the thinnest, because
the server picks the format and the promise has to hold either way. With this build's list that is
**192 000 bytes**. An explicit `8_000` is honoured verbatim; an explicit `100_000_000` clamps back to
192 000.

The thinnest entry is Opus with no declared bitrate, which the SDK values at a conservative 64 kbps
fallback. Declaring `AudioFormat.Bitrate` would tighten the figure and is deliberately not done here.

Both sides now take the SDK's default: `CreatePipeline` passes no `bufferCapacityMs`, so the decoded
ring and the advertisement are derived from the same 30 s. Passing a different number to one of them
is how the two come to disagree — which is exactly what the old `BufferCapacityMs = 8_000` constant
did, advertising about one second of Opus while calling itself eight.

### Arbitration: a dialled session has to be adopted by the host

`SendspinHostService` arbitrates incoming server connections against the ones it accepted. It cannot
see a session this application dialled itself, so a server connecting in while such a session was
playing used to be arbitrated as "no existing connection" — accepted, registered and announced —
resetting the shared clock synchroniser and pipeline under a stream that was still running
(SDK #253).

9.3.1 added `AdoptClientInitiated` / `ReleaseClientInitiated` for this, and
`SendspinPlayerService.ConnectAsync` calls the first once the connect completes;
`DisconnectAsync` and the disposal path call the second. **Dropping `ConnectionMode.Auto` did not
make this unnecessary** — advertising is the default mode, and both the manual-connect path and the
auto-connect policy dial out regardless of mode, so a host and a client still coexist.

Two details worth not rediscovering. Ownership does not move: the SDK never disconnects or disposes
an adopted client, so the existing teardown is unchanged — `HostArbitrationTests` asserts that
adoption registers no connected server and raises no event. And arbitration is keyed by server id,
which the manual-connect path does not have up front; the id is taken after the connect from
`client.ServerId ?? serverId`, and a session with no id from either source is left unadopted and
logged rather than adopted under a placeholder the release could never match.

### `waitForConvergence` does not mean what it looks like

On timeout the SDK logs `[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full
convergence.` and proceeds. There is no value meaning "never proceed unconverged", and
`SendspinClientService` owns the `client/state` message that reports availability. See
`docs/NEXT_STEPS.md` item 2.

Still true on 9.3.2, checked rather than assumed: 9.3.0 reworked the filter's probe cadence — a link
that stays noisy now falls back to the steady-state interval and withholds `IsClockSynced` — but the
timeout string above is byte-identical in both assemblies.

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
- `SyncCorrectionCalculator` **does** exist, in 9.1.0 and still in 9.3.2. An earlier planning note
  said it did not.
- `NotifyExternalCorrection` became **stats-only** in 9.3.0. It no longer moves the read cursor that
  `SyncErrorMicroseconds` is measured against, because `ReadRaw` already credits every sample it
  hands over and a corrector sizes its read to the correction. Up to 9.2 the same frames were counted
  twice, so the reported error converged at *twice* the physical correction — this player read near
  zero while sitting about half the drift out of the group. The bump fixes it with no code change
  here.
- Several members added by 9.2/9.3 are documented in the SDK's XML but declared `internal`, because
  the 9.x line freezes its published surface: `SyncCorrectionOptions.HardSyncThresholdMicroseconds`,
  `EffectiveMaxSpeedCorrection` and `ExceedsSpecSpeedCap`;
  `ClientCapabilities.TruthfulBufferCapacityBytes`, `BufferCapacityWasClamped` and
  `ConfiguredBufferCapacity`; and on `AudioBufferStats`, `HardSyncStalled`, `HardSyncCount`,
  `LateChunksDropped` and `ContentHolesDetected`. Reading the XML docs is not enough to know what a
  9.x caller can actually reach — check the accessibility.

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

**All of the above is now measured rather than reviewed.** First execution of this backend was on
Fedora 44 / PipeWire 1.6.8 / OpenAL Soft 1.24.2, against Music Assistant over plain `ws://`:

```
OpenAL Soft timing extensions: ALC_SOFT_device_clock available, AL_SOFT_source_latency available,
AL_SOFT_source_start_delay available
Audio output ready: 48000 Hz, 2 ch, measured latency 21 ms (+0 ms manual), timing source audio-clock
```

(That line has since gained the negotiated sample format — see *Linux — capability discovery goes
through PipeWire* below. The rest of the record stands as it was.)

All three hand-bound extensions resolve, `TimingSourceName` reports `audio-clock`, enumeration lists
both outputs with the right one defaulted, and `SwitchDeviceAsync` reopens either device with
playback uninterrupted and the clock re-anchored. Nothing in this section had to be revised in the
light of running it, which is worth recording as much as a correction would have been.

### Linux — the `libopenal` that loads is the system one, not the bundled one

`Silk.NET.OpenAL.Soft.Native` ships `runtimes/linux-x64/native/libopenal.so`, so it is tempting to
assume that is what runs. It is not, wherever a system OpenAL exists: Silk.NET asks for the SONAME
`libopenal.so.1`, the RID asset is named `libopenal.so`, and so the dynamic loader answers with
`/usr/lib64/libopenal.so.1` first. Confirmed from `/proc/<pid>/maps` — the running player has the
host's 1.24.2 mapped, along with `libpipewire-0.3.so`, and never touches the bundled copy.

This matters because the two builds are not equivalent. The bundled 1.23.1 has **no PipeWire
backend** (`alsa`, `jack`, `null`, `pulse`, `wave` only), while the host's 1.24.2 does. So the
fallback that takes over on a machine without a system OpenAL is the *less* capable library, which
is the opposite of the usual assumption — and it is why `packaging/deb/control.template` depends on
`libopenal1` rather than relying on the bundled asset.

The same asymmetry drives the Flatpak's audio grant: `org.freedesktop.Platform` 25.08 ships OpenAL
Soft 1.24.3 built with the **pulse backend only**, so `--filesystem=xdg-run/pipewire-0` on its own
reaches nothing inside the sandbox. See the reasoning kept alongside both grants in
`packaging/flatpak/io.sendspin.client.yml`.

### Linux — capability discovery goes through PipeWire, playback stays on OpenAL

**OpenAL cannot answer what the device supports, and its one rate-shaped property actively lies.**
There is no accepted-rate query and no channel-count query in OpenAL at all. `ALC_FREQUENCY` looks
like it might serve, and does not: open a device with `ALC_FREQUENCY` set and read it back, and it
returns *exactly what was asked for*, whatever the hardware can do. Measured on both outputs of the
dev machine:

```
device: Ryzen HD Audio Controller Analog Stereo
   req  96000 -> ALC_FREQUENCY  96000
   req 192000 -> ALC_FREQUENCY 192000
device: Radeon High Definition Audio Controller Digital Stereo (HDMI 3)
   req  96000 -> ALC_FREQUENCY  96000
   req 192000 -> ALC_FREQUENCY 192000     <-- this sink's node caps at 48 kHz
```

OpenAL Soft runs its own mixer at whatever rate it was asked for and resamples into the backend, so
it can never answer "no". It is unusable as a gate on an advertisement. The sink node's PipeWire
`EnumFormat` is the real list, and reading it opens no device — which is what made the OpenAL
enumerator's old restraint ("only probe the default device, opening one has side effects")
unnecessary rather than merely inconvenient.

**A node's `EnumFormat` is only half the answer; the daemon's clock policy is the other half.**
PipeWire clocks the *entire graph* at one rate, chosen from `clock.allowed-rates`, and a stream at
any other rate is resampled on the way in. On the dev machine the analog sink's `EnumFormat` is
`rate: Range{default 48000, min 48000, max 192000}` — the converter genuinely reaches 192 kHz — while
the daemon ships with:

```
clock.rate           = 48000
clock.allowed-rates  = [ 48000 ]
```

Playing a 96 kHz file and sampling the node's negotiated `Format` three times during playback showed
`48000` throughout: PipeWire resampled and never renegotiated. Setting `clock.force-rate 96000` at
runtime and replaying moved the node to `96000`. **So the hardware can and the daemon will not**, and
advertising 96/24 on that box would buy nothing but a resampler. What is reported is therefore the
*intersection* of the node's `EnumFormat` and the rates the daemon's clock policy will grant, which
is the only figure that means "this plays without a resampler in the path".

Two consequences worth keeping:

- **`MixSampleRate` is now answered for every device, not just the default.** The graph clock is
  shared, so one `pw-dump` gives every node's mix rate without opening any of them. But it is
  withheld from a sink that cannot *meet* the graph rate — force the graph to 96 kHz and the HDMI
  sink, capped at 48 kHz, must not report 96 kHz as its mix rate. That bug was caught by running the
  probe against real hardware, not by reading the code.
- **Where PipeWire is absent the fields stay empty** and the client falls back to the pre-PipeWire
  behaviour. A plain-ALSA or PulseAudio box is a supported configuration, not a degraded one.

The reader shells out to `pw-dump` rather than binding libpipewire: binding it means a main loop, a
roundtrip and a registry listener on a background thread to answer a question asked once per
enumeration. The parsing is in `Sendspin.Core` (a pure function over the JSON, unit-tested against
the shapes a live daemon emits); only the process invocation lives in `Sendspin.Platform.Linux`.

**The render path is float32 where the driver has it.** `AL_EXT_FLOAT32` is present on OpenAL Soft
1.24.2, and since the sample source is already `float[]`, the float path performs *no* per-sample
conversion where the int16 path performed one — it is both the wider and the cheaper route. The
int16 path is kept for drivers without the extension. Note the extension must be queried with
`alIsExtensionPresent` on a **current context**: `alcIsExtensionPresent` answers "no" for
`AL_EXT_FLOAT32`, `AL_EXT_DOUBLE` and `AL_EXT_MCFORMATS` on a driver that plainly has all three,
because those are AL extensions rather than ALC ones. The negotiated format is reported alongside
the rate:

```
Audio output ready: 96000 Hz, 2 ch, float32 out (stream 24-bit), measured latency 13 ms
(+0 ms manual), timing source audio-clock
```

**Opus was being advertised at a rate its own decoder rejects.** Unrelated to PipeWire, found while
checking what the SDK can actually decode: `OpusDecoder` throws
`ArgumentException("Sample rate is invalid (must be 8/12/16/24/48 Khz)")` on construction for
anything outside that set, and this player advertised `opus/44100` on *every* platform. A server
that picked it got a decoder that threw before the first sample — a dead stream, not a degraded one.
Opus is now constrained to the rates it accepts. The SDK's PCM and FLAC decoders were checked the
same way and do handle 24-bit at 48/96/192 kHz, which is what makes the hi-res tier honest on the
decode side.

### `SupportedSampleRates` means "runs natively as configured" — on all three platforms

The field is not "rates the hardware is capable of" and not "rates that will play". Almost any rate
will play. It answers one question: **if the server sent audio at this rate right now, would
anything resample it before the converter?** If the platform would have to be reconfigured first —
a device rate switched, a daemon setting changed, an exclusive-mode stream taken — the rate does not
belong in the list, however capable the hardware is.

| platform | source | why it satisfies the contract |
| --- | --- | --- |
| Linux | sink `EnumFormat` ∩ PipeWire's `clock.allowed-rates` | the node's own list is the hardware's capability, not the daemon's willingness |
| Windows | the engine mix rate | shared mode renders at the mix format and converts everything else |
| macOS | the current nominal rate only | this player never sets the nominal rate, so every other available rate goes through AUHAL's converter |

**This was written down because its absence shipped a bug.** The field silently meant something
different per enumerator, and the shared tiering in `PlayerCapabilities` could not tell. macOS filled
it from `DeviceAvailableNominalSampleRates` — rates the device could be *switched* to — while
`AuhalRenderPlayer` only ever *reads* `DeviceNominalSampleRate` and never sets it. So a Mac running
at 48 kHz whose output also lists 96 kHz advertised `flac/96000/16` **ahead of** `flac/48000/16`, and
CoreAudio resampled it straight back down: more bytes over the network to reach a resampler, at no
gain in depth — the exact failure the hi-res work exists to prevent.

Windows escaped only by accident: its shared-mode `IsFormatSupported` probe happened to admit little
beyond the engine mix rate. "Safe by accident" stopped being good enough once the advertisement
began *leading* with the high-resolution tier instead of appending it, so that probe was narrowed to
the mix rate too — which loses nothing, because an endpoint genuinely running at 96 kHz reports
96 kHz as its mix rate and still earns its tier.

The fix is in the enumerator, not in the shared tiering, and that placement is the point. On Linux a
rate above the current one genuinely *is* native when the daemon's clock policy permits it, so
"listed but not the current rate" is legitimate there and illegitimate on macOS. Shared code cannot
distinguish the two; only the enumerator knows which it is. `CoreAudioSampleRates.ResolveNative`
holds the macOS decision and lives in `Sendspin.Core` so it can be tested, the same split the
PipeWire parsing uses.

The shared side was tightened too, but for a different gap: a rate reaches the hi-res tier only if
it is in `SupportedSampleRates` *or* is the device's current `MixSampleRate`. The rate threshold
alone was never a gate, because `ResolveSampleRates` promotes the mix rate and copies the device
list before appending the two hardcoded fallbacks — so "above 88.2 kHz" only excluded the fallbacks,
and the tier was resting on every enumerator happening to include its own mix rate in its list.

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
