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
you when one of three parallel files falls behind. This is why the Linux windowing-backend choice
lives in `Core/Platform/LinuxWindowingBackend.cs` with a test, rather than in the Linux file where
it started.

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

## UI shell

What the windowing layer actually does when asked to follow the desktop, and what the living-backdrop
effect loop costs. Measured on the dev machine — Fedora 44, KDE Plasma 6.7.4 on Wayland,
`xdg-desktop-portal` 1.22.1 with the KDE 6.7.4 and GTK 1.15.3 backends, Avalonia 12.1.1, an NVIDIA
RTX 4060 plus an AMD Phoenix iGPU, windows opened on a 2560×2160@60 Hz output at scale 1.25 — with a
throwaway probe kept under `scripts/spike/ShellSpike/` so any of it can be re-run:

```
dotnet run --project scripts/spike/ShellSpike -- theme | font | chrome | clock | effects <case>
```

Every mode prints `[spike]` lines; the numbers below are those lines. `SENDSPIN_X11=1` selects the X11
head exactly as it does for the player. **GNOME, Windows 11 and macOS were not available and are marked
unmeasured**, each with the procedure — which is the same probe, run there.

One fact that frames all the rendering numbers: on this box **both Linux heads render on the AMD
iGPU**. The probe lists the process's open DRM nodes after a frame, and it is `/dev/dri/renderD128`
(vendor `0x1002`) on Wayland, on X11 and inside the Flatpak; the NVIDIA EGL vendor library is mapped
but no NVIDIA render node is ever opened. "GPU" below means the iGPU.

### Theme and accent on Linux — the portal is read, live, on both heads

`Avalonia.FreeDesktop`'s `DBusPlatformSettings` reads `org.freedesktop.portal.Settings` once at
start-up (`ReadOne org.freedesktop.appearance color-scheme` / `accent-color`) and subscribes to
`SettingChanged`. With `RequestedThemeVariant="Default"` that is the whole mechanism; there is nothing
else to wire. Measured by running the probe on both heads at once and flipping the desktop from a
third terminal:

| Command (wall clock) | Portal reports | Wayland head `ActualThemeVariant` | X11 head `ActualThemeVariant` |
|---|---|---|---|
| `plasma-apply-colorscheme BreezeLight` at 14:49:53.425 | `color-scheme` → `2` | `Light` at 14:49:53.596 (**+171 ms**) | `Light` at 14:49:53.593 (**+168 ms**) |
| `plasma-apply-colorscheme BreezeDark` at 14:50:03.781 | `color-scheme` → `1` | `Dark` at 14:50:03.905 (+124 ms) | `Dark` at 14:50:03.902 (+121 ms) |

Both heads flip live and at the same moment, because the portal read is D-Bus and identical either
way. `Application.ActualThemeVariantChanged`, `Window.ActualThemeVariantChanged` and
`PlatformSettings.ColorValuesChanged` all fire; a screenshot of the player in each state is in
`docs/screenshots/spike/app-{wayland,x11}-{light,dark}.png`.

**Accent.** `PlatformSettings.GetColorValues().AccentColor1` is exactly the portal's `accent-color`:
`(0.239216, 0.682353, 0.913725)` arrives as `#3DAEE9`, Fluent's `SystemAccentColor` resource reads
`#3DAEE9`, and the pixel the probe samples from an `accent`-classed `Button` and from a `Border`
bound to that resource is `#3DAEE9` — portal, resource and rendered colour agree byte for byte. But
**what the KDE portal serves as the accent is the colour scheme's selection colour, not the accent
the user picked**, and it only refreshes on a scheme change. Three experiments:

- Switching schemes: `plasma-apply-colorscheme Nordic` at 14:53:07.719 → portal `accent-color`
  `(0.560784, 0.737255, 0.733333)` = Nordic's `Colors:Selection/BackgroundNormal=143,188,187` → the
  app's `SystemAccentColor` and the rendered pixel are `#8FBCBB` at 14:53:07.841 (**+122 ms**).
- `plasma-apply-colorscheme --accent-color '#E9643D'` rewrites the `Colors:*` groups in `kdeglobals`
  (`Colors:Selection/BackgroundNormal` became `169,76,49`) but never writes `General/AccentColor`,
  and the portal kept answering `#3DAEE9` five seconds later; the app saw a `ColorValuesChanged`
  with the unchanged accent. Re-applying the *same* scheme afterwards does not undo the tint
  (`plasma-apply-colorscheme` skips a scheme whose hash already matches); switching to another scheme
  and back does.
- Writing `General/AccentColor=233,100,61` with `kwriteconfig6 --notify` changes nothing until the
  next scheme change; then the portal serves the **tinted selection colour** — `#EF9277`
  (`239,146,119`) under BreezeLight, `#A94C31` (`169,76,49`) under BreezeDark — never `#E9643D`
  itself.

So on Plasma the app's accent is Plasma's *highlight* colour, a light or dark derivative of the user's
choice that changes with the variant. The reskin should treat `SystemAccentColor` as "the desktop's
highlight", not as a saturated brand colour. Whether System Settings' accent picker triggers the
scheme re-apply that makes the portal notice is **unmeasured** (it needs a click): pick an accent
there while `ShellSpike theme --seconds 30` runs and read the `ColorValuesChanged` lines.

**No portal.** With `DBUS_SESSION_BUS_ADDRESS` unset — and, in case the library falls back to
`$XDG_RUNTIME_DIR/bus`, also with it pointed at `unix:path=/nonexistent` — both heads settle at
`ActualThemeVariant=Light`, `AccentColor1=#0078D7`, resource and rendered pixel `#0078D7`. That is
`DefaultPlatformSettings.GetColorValues()` (variant `Light`, no accent) plus Fluent's built-in accent.
Taking the bus away also takes MPRIS, notifications and the tray with it, so this is the spike's
probe of the fallback, not a supported configuration.

**First paint can be the fallback.** The portal read is asynchronous and the window is shown before it
completes: in one of the seven portal-backed Wayland launches the `opened` report was `Light / #0078D7` and the `Dark /
#3DAEE9` change arrived **22 ms** later (14:52:20.094 → 14:52:20.116). Anything that captures the
variant on `Opened` — a splash colour, a first-frame screenshot — should wait for
`ActualThemeVariantChanged` instead.

**Flatpak.** The probe was published self-contained and wrapped with the player's own manifest
(same runtime `org.freedesktop.Platform//25.08`, same `finish-args`, only the command changed), built
with `org.flatpak.Builder` from Flathub because `flatpak-builder` is not installed here. Inside the
sandbox the portal round-trip works unchanged: `Dark / #3DAEE9` at start, `Light` **141 ms** after
`plasma-apply-colorscheme BreezeLight` (14:57:19.809 → 14:57:19.950), `Dark` 150 ms after the
reverse. No extra permission was needed — the Settings portal is reachable from every sandbox.

**GNOME — unmeasured.** Same property set. Procedure: run `ShellSpike theme --seconds 30`, then
`gsettings set org.gnome.desktop.interface color-scheme prefer-dark` / `default` and, on GNOME 47+,
`gsettings set org.gnome.desktop.interface accent-color teal`; the portal's GTK/GNOME backend serves
both keys, so the expectation is the same live flip. The thing to check is the accent: the GNOME
backend is expected to serve the accent the user picked rather than a derived highlight, in which
case `AccentColor1` is a saturated colour there — the opposite of Plasma, and the reskin has to be
happy with both.

**Windows 11 and macOS — unmeasured.** Same property set (`RequestedThemeVariant="Default"`, and
`SystemAccentColor` for the accent). Procedure: `dotnet run --project scripts/spike/ShellSpike --
theme --seconds 60`, flip Settings › Personalization › Colors (Windows) or System Settings ›
Appearance (macOS) and read the `ColorValuesChanged` lines. Neither backend goes through a portal,
so the no-portal fallback above is Linux-only.

### System font — Inter wins only because Fluent asks for it first

`.WithInterFont()` does one thing: it registers `InterFontCollection` under the `fonts:Inter` key. It
sets no default. What makes Inter the face of every control is **Fluent's own resource**
`ContentControlThemeFontFamily = "fonts:Inter#Inter, $Default"` — a composite whose first entry
resolves only when that collection exists. `$Default` is `FontManagerOptions.DefaultFamilyName` if
set, otherwise Skia's `SKTypeface.Default.FamilyName`, which on Linux is fontconfig's answer to an
empty pattern — the same as `fc-match sans-serif` — and **not** the desktop's configured UI font
(`kreadconfig6 --group General --key font` says `Noto Sans,10`; on this host the two happen to
agree). Measured with `ShellSpike font`, which resolves the glyph typeface a `TextBlock` with no
`FontFamily` actually ends up with:

| Where | `$Default` resolves to | Default `TextBlock`, with `WithInterFont()` | … with `SPIKE_NO_INTER=1` | `fc-match sans-serif` there |
|---|---|---|---|---|
| Host | Noto Sans | Inter | Noto Sans | `NotoSans-Regular.ttf` |
| Flatpak (`org.freedesktop.Platform//25.08`) | **DejaVu Sans** | Inter | **DejaVu Sans** | `DejaVuSans.ttf` |
| AppImage — built with `appimagetool` from the probe's publish in the `scripts/build-appimage.sh` AppDir layout with its `AppRun`, and run as the `.AppImage` | Noto Sans | Inter | Noto Sans | host's |

The Flatpak row is the reason to measure: the sandbox brings its own fontconfig, and its
`sans-serif` is DejaVu Sans, so "the platform default" inside the Flatpak is a different face from
the desktop around it. Host fonts *are* visible from the sandbox (873 `fc-list` entries, mounted under
`/run/host/fonts`), which is also why `Inter` resolves *by plain name* in all three columns — this
host has Inter installed as a system font. Do not read that as a guarantee; on a host without it,
only the embedded `fonts:Inter#Inter` resolves. The AppImage sees the host's fontconfig unchanged.
Character fallback is unaffected in every configuration: U+65E5 (日) resolves to `Noto Sans CJK JP`
from the system collection whichever face is primary.

**The shape for "platform default, Inter as fallback".** `FontManagerOptions` alone cannot do it,
because the Fluent resource puts Inter first. Both halves are needed, and this is the pair that was
measured working (`SPIKE_FONT_SHAPE=a`: `TextBlock` → `Noto Sans`, `TryMatchCharacter('A')` → `Inter`):

```csharp
// Program.cs — WithInterFont() stays; it only registers the embedded collection.
AppBuilder.Configure<App>()
    ...
    .WithInterFont()
    .With(new FontManagerOptions
    {
        // DefaultFamilyName deliberately unset: null means "ask the platform", which is the point.
        FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("fonts:Inter#Inter") } },
    })
```

```xml
<!-- App.axaml — overrides Fluent's "fonts:Inter#Inter, $Default" -->
<Application.Resources>
    <FontFamily x:Key="ContentControlThemeFontFamily">$Default</FontFamily>
</Application.Resources>
```

`FontFallbacks` is consulted *first* in `FontManager.TryMatchCharacter`, before the family's own
composite list and before the system collection — which is why the `'A'` probe answers `Inter`. That
only matters for a glyph the primary face lacks; a face that has the glyph never reaches fallback.
The composite alternative (`$Default, fonts:Inter#Inter` as the resource, no `FontManagerOptions`;
`SPIKE_FONT_SHAPE=b`) also renders the platform face and is one line instead of two, but leaves
Inter behind the whole system collection in the fallback order.

**Windows and macOS — unmeasured.** The intent is Segoe UI Variable on Windows and the system font on
macOS. What `$Default` resolves to there is whatever `SKTypeface.Default.FamilyName` says — on
Windows that may well be plain `Segoe UI` rather than the Variable face, in which case
`DefaultFamilyName` has to be named per platform. Procedure: `dotnet run --project
scripts/spike/ShellSpike -- font` and read the `FontManager.DefaultFontFamily` line.

### Decorations and client-area extension — the hint does nothing under KWin

In 12.1.1 both Linux backends honour `ExtendClientAreaToDecorationsHint` **only when Avalonia is
already drawing the decorations itself**. Wayland: `WindowImpl.IsClientAreaExtendedToDecorations`
returns the hint only when the compositor answered *client-side* over `zxdg_decoration_manager_v1`,
and KWin answers server-side. X11: `SetExtendClientAreaToDecorationsHint` is guarded by
`X11PlatformOptions.EnableDrawnDecorations`, which is `[Experimental("AVALONIA_X11_CSD")]` and off.
`SetExtendClientAreaTitleBarHeightHint` is an empty method on both. Measured with `ShellSpike chrome`
(hint on, title-bar hint −1), screenshots in `docs/screenshots/spike/chrome-*.png`:

| Head | Options | `IsExtendedIntoWindowDecorations` | `WindowDecorationMargin` | `FrameSize` | What is on screen |
|---|---|---|---|---|---|
| Wayland | default | `False` | `0,0,0,0` | not reported | KWin's Breeze title bar, content starts below it, moves and resizes by the compositor |
| Wayland | `ForceDrawnDecorations` | `True` | `8.8,39.2,8.8,8.8` | not reported | Avalonia's own caption and buttons; the content's top 48 px sit under that caption |
| X11 | default | `False` | `0,0,0,0` | `880×628` | KWin's title bar (28 logical px), content below it |
| X11 | `EnableDrawnDecorations` | `True` | `8.8,39.2,8.8,8.8` | `880×600` | Avalonia's own caption, as on Wayland |

So the top inset the hint "comes out as" on Linux is **zero** unless native decorations are given up
altogether, and then it is 39.2 px of Avalonia chrome, not a compositor title bar with art behind it.
The macOS-style "art bleeds up behind the traffic lights, system buttons stay" composition does not
exist on Linux in this Avalonia; the choice is native decorations *or* Avalonia-drawn ones. Leaving
the hint set is harmless under KWin, but see GNOME. Whether the CSD window still moves by its caption
was not exercised (it needs a drag); Avalonia's managed decorations call `BeginMoveDrag` for it.

Two things read off the same run, both worth keeping:

- `ActualTransparencyLevel` is **`Transparent`** on both heads with an empty `TransparencyLevelHint`:
  the window has an alpha channel by default, so a partially transparent `Background` shows the
  desktop through, not the window's own backdrop. Anything translucent inside the window needs an
  opaque root behind it.
- On the Wayland head `RenderScaling` reads `1` in `OnOpened` and only becomes the output's 1.25
  after the first configure (the screenshots are the same pixel size on both heads). Do not size
  bitmaps from it that early.

**GNOME — unmeasured, and the one place the hint bites.** Mutter does not implement
`xdg-decoration`, so the Wayland backend takes the client-side answer and draws its own chrome
whether or not the hint is set; with the hint set it additionally extends the client area (the
`ForceDrawnDecorations` row above is what GNOME should look like). Procedure: `ShellSpike chrome`
under GNOME, read `IsExtendedIntoWindowDecorations` and `WindowDecorationMargin`, and look at whether
the title text is drawn by Avalonia.

**macOS — unmeasured.** Intended: `ExtendClientAreaToDecorationsHint="True"`,
`ExtendClientAreaTitleBarHeightHint="-1"`, no custom caption buttons, art allowed under the title
region. Check: `ShellSpike chrome`, expect `IsExtendedIntoWindowDecorations=True`, a non-zero top in
`WindowDecorationMargin` (that is the traffic-light strip), and the red band in the screenshot sitting
behind the traffic lights with the window still moving by that strip.

**Windows 11 Mica — unmeasured.** Intended: `TransparencyLevelHint="Mica, None"`
(`WindowTransparencyLevel.Mica` first, `None` as the pre-22H2 fallback — the property is an ordered
list and `ActualTransparencyLevel` reports which one was granted) with a `Background` of about 35 %
opacity so Mica shows through. Check: `SPIKE_TRANSPARENCY=mica,none ShellSpike chrome` on Windows 11
22H2+ and read `ActualTransparencyLevel=Mica`; on Windows 10 expect `None` and an opaque window, and
confirm the 35 % background then composes over `TransparencyBackgroundFallback` rather than over
nothing.

### Effect loop cost — and the clock it must not be driven by on Wayland

The living backdrop as planned — three `RadialGradientBrush` ellipses with per-frame
`ScaleTransform`/`TranslateTransform` and colour updates, re-armed from
`TopLevel.RequestAnimationFrame` — was built as `ShellSpike effects <case>` in an 880×600 window and
measured as **process CPU milliseconds per rendered frame** (`Process.TotalProcessorTime` delta over
the frames, after a 3 s warm-up, `DOTNET_TieredCompilation=0` so the JIT thread is not in the
number). The renderer's own `RendererDebugOverlays.Fps` was screenshotted alongside and read 58–60 in
every configuration, so "per frame" below is per presented frame.

**First, the clock.** `ShellSpike clock` drives a counter with each candidate for three seconds:

| Clock | Wayland head | X11 head |
|---|---|---|
| `RequestAnimationFrame` re-armed in the callback | **25 124 Hz** (0.04 ms/tick) | 59.6 Hz |
| `DispatcherTimer` 16 ms, any priority | **5–7 Hz** (140–200 ms/tick across runs) | 62.3 Hz |
| `DispatcherTimer` 100 ms | **4.3 Hz** (231 ms/tick) | 10.0 Hz |
| `DispatcherTimer` 500 ms | **1.7 Hz** (600 ms/tick) | 2.0 Hz |
| `System.Threading.Timer` 16 ms → `Dispatcher.UIThread.Post` | 62.3 Hz | 62.3 Hz |
| `Task.Delay(16)` loop on the UI thread | 61.3 Hz | 61.3 Hz |
| Avalonia `Animation`, 1 s loop — property change rate | **24 268 Hz** | 60.3 Hz |

On the Wayland head in 12.1.1, **`RequestAnimationFrame` is not tied to a frame**: the callback runs
again as soon as it is re-armed, 25 000 times a second, and the renderer still presents 60 of them
(the FPS overlay read `Frame #74` while the callback counter read 25 259). The plan's loop as written
costs 0.04 ms × 25 000 = **a full core** on Wayland to animate nothing faster. Avalonia's own
`Animation`/`Transitions` clock has the same problem there, and `DispatcherTimer` has the opposite
one — every due time lands on a coarse boundary about 100–200 ms late (16 → 140–200 ms depending
on the run, 100 → 231 ms, 500 → 600 ms; the last two reproduce exactly, the first varies), which
means the player's two 500 ms `DispatcherTimer`s — the progress bar and the
diagnostics refresh — already tick every 600 ms on the Wayland head today. X11 is correct on every
row. Until `Avalonia.Wayland` is fixed (re-check each bump with `ShellSpike clock`), **the backdrop has to be
paced by a thread-pool timer posting to the dispatcher**, which is the one clock that behaves on both
heads; that is the driver every number below uses (`SPIKE_DRIVER=threadtimer`).

**Then the cost**, CPU ms per frame at 60 Hz. "Software" on X11 is
`X11PlatformOptions.RenderingMode = [Software]`; on Wayland the backend has no such option, so EGL
was made to fail (`__EGL_VENDOR_LIBRARY_FILENAMES=/nonexistent`) and the backend fell through to its
`wl_shm` framebuffer surface — the probe confirms only `libEGL.so.1` and `libSkiaSharp.so` mapped and
no DRM node open, which is what an AppImage on a box without working GL gets.

| Case | Wayland, GPU | X11, GPU | X11, software | Wayland, shm |
|---|---|---|---|---|
| `baseline` — a counter `TextBlock` and nothing else | 1.72 | 1.89 | 4.86 | 4.17 |
| `gradient-mutate` — three ellipses, `GradientStop.Color` mutated | 1.81 | 1.91 | 12.50 | 11.12 |
| `gradient-swap` — same, `Fill` replaced with a new `ImmutableRadialGradientBrush` | 1.82 | 2.05 | 12.50 | 11.38 |
| `glow-boxshadow` — 320 px tile, `BoxShadow.Blur` animated 10–60 | 1.95 | 1.95 | 6.32 | 5.36 |
| `glow-dropshadow` — same tile, `DropShadowEffect.BlurRadius` animated | 1.96 | 2.05 | 12.26 | 10.44 |
| `blur-once` — 64 px scaled artwork, `Image` with `BlurEffect(32)` behind the counter | 1.87 | 1.94 | 4.93 | 4.32 |

(Driven by a `DispatcherTimer` instead, every X11 figure reads higher — 3.4–4.1 on GPU, 6.6–16.1 on
software — and the difference is the dispatcher timer's own dispatch, not the effect; so the table
uses the thread-pool timer in every column.)

What the table answers:

- **Mutating gradient stops versus swapping the brush: no difference.** 1.81 vs 1.82 ms on the
  Wayland GPU column, 1.91 vs 2.05 on X11, 12.50 vs 12.50 on software, 11.12 vs 11.38 on shm. Pick
  whichever reads better; there is no performance argument.
- **`BoxShadow` is the cheap glow, and only on software does it matter.** On GPU the two are
  identical (1.95 vs 1.96 ms, both within 0.25 ms of the empty window). On software raster the
  `DropShadowEffect` glow costs **7.4 ms** over baseline on X11 and 6.3 ms on shm; the `BoxShadow`
  glow **1.5 ms** and 1.2 ms. The plan's assumption holds where it costs anything.
- **The scaled-and-blurred artwork is a one-off.** `blur-once` sits within 0.15 ms of baseline in
  every column (+0.15, +0.05, +0.07, +0.15), so the `BlurEffect` on a 64 px bitmap is paid once and
  cached, not re-run per frame. (`Bitmap.CreateScaledBitmap` throws *"Invalid source bitmap type"*
  for a `WriteableBitmap`; it wants a decoded `Bitmap`, which real artwork already is.)
- **The three-ellipse backdrop is 7–7.6 ms of CPU per frame on software rendering** at 880×600 —
  close to half the 16.7 ms frame on one core, and it scales with window area. On GPU it is 0.1 ms.
  So the backdrop is free where GL works and needs a "reduced motion / no GL" switch where it does
  not; the probe's `mapped graphics libraries` line is how to tell the two apart at runtime.
