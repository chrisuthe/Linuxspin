# Next steps

Work this rebuild deliberately did not do, in the order it unblocks the most. Each item says what
is blocked, why it could not be finished here, and what the first concrete action is.

Nothing on this list is a discovered surprise — every one of them was a known limit before the work
started or was found and recorded during it. `docs/COMPLIANCE.md` is the companion document: it
states what the shipped code does and does not implement.

---

## 1. Transport encryption and pairing — blocks calling this spec compliant

**Status: blocked on the SDK, not on effort.**

The spec mandates Noise `KKpsk2` over `ws://` (server always initiator), requires clients to
implement Pairing PSK, and requires the management role of all clients. `Sendspin.SDK` 9.3.2 ships
none of the three, and will not: 9.3.2 is the **9.x legacy line**, a curated backport for apps that
cannot take 10.0's breaking renames. That work is merged on `sendspin-dotnet` `main` (PRs #67 and
#73) but **no 10.x is published on nuget.org — not even a prerelease**, so this is blocked on
something nobody can resolve today, not merely on an upgrade nobody has done.

**This repository does not hand-roll it, and should not.** Noise and CPace are cross-client protocol
concerns; a second implementation of a key exchange is a liability, not a feature. If the SDK is
missing something, the fix goes in the SDK.

**Consequence today:** against a server that *requires* encryption these builds **will not connect
at all**. Label releases **pre-encryption**. Do not describe them as spec compliant.

**First action:** watch for the first published `Sendspin.SDK` 10.x. When it lands:

1. Bump the single pin in `Directory.Packages.props`.
2. Expect the client and host service constructors to change shape; the call sites are
   `SendspinPlayerService.StartAdvertisingAsync` and `ConnectAsync`.
3. Add the pairing UI. There is no design for it yet — deliberately, because designing against an
   unpublished API is how you build the wrong thing.
4. Add the `management@v1` role to `PlayerCapabilities.Build`.
5. Update the role table in `docs/COMPLIANCE.md` and drop the pre-encryption labelling.

**Already prepared for, so it is not a migration surprise:** `ConnectionMode.Auto` is removed in
10.0.0, and this repository no longer uses it — the enum is referenced only by the migration in
`PlayerSettings.ApplyMigrations`, which rewrites a persisted `Auto` to `AdvertiseOnly`. Delete that
migration when the enum member goes, not before: it is what carries an existing install across.

---

## 2. File the upstream convergence-gating issue

**Status: needs a human. An agent may not open issues (`AI_POLICY.md`).**

The spec requires a player to withhold availability until its time filter has converged, with no
"timed out, proceed anyway" path. This **cannot be satisfied from this repository** against SDK
9.3.2 — re-checked against the shipped assembly rather than carried forward, because 9.3.0 reworked
the clock filter's probe cadence. The timeout path is unchanged: the log string below is
byte-identical in 9.1.0 and 9.3.2. The pipeline is constructed with `waitForConvergence: true`, but
on timeout the SDK logs

```
[ClockSync] Timeout after {ElapsedMs}ms. Starting playback without full convergence.
```

and proceeds — and `SendspinClientService` owns the `client/state` message that reports
availability. There is no parameter value meaning "never proceed unconverged".

The timeout is left at the SDK default of 5000 ms **on purpose**. Inflating it would hide the
behaviour rather than fix it, and would trade a known bounded gap for an unbounded startup stall.

**First action:** open an issue on `Sendspin/sendspin-dotnet` asking for either a
`convergenceTimeoutMs` value that means "wait indefinitely", or a way for the embedder to gate the
availability report itself. Then put the issue number in the corresponding section of
`docs/COMPLIANCE.md`, which currently says it has not been filed.

---

## 3. A Developer ID certificate for macOS signing

**Status: blocked on a resource, not on code. CI already builds the bundle.**

CI produces an unsigned `.app` and dmg, and checks the things that are silently wrong rather than
loudly wrong (a symlinked executable, missing local-network keys). Signing, notarization and
stapling need a Developer ID certificate.

**Ad-hoc signing is not a partial substitute, and using it would make things worse.** TCC keys the
local-network grant to the code signature, so an ad-hoc hash that changes on every rebuild produces
a grant that silently stops applying — which yields *false* discovery results rather than merely
unsigned builds. There is also no way to reset an app's local-network grant, so a bad test poisons
the machine.

**First action:** obtain a Developer ID Application certificate and add it to CI as a secret. Then,
in `build-macos`:

- Entitlements must contain **`com.apple.security.cs.allow-jit` and nothing else**.
  `allow-unsigned-executable-memory` was removed from .NET in 2021, and
  `disable-library-validation` is avoidable by re-signing Microsoft's `libSkiaSharp` and
  `libHarfBuzzSharp` dylibs with the project identity.
- Sign **inside-out, never `--deep`** (Apple: "Do not use the `--deep` argument").
- Archive with `ditto -c -k --sequesterRsrc --keepParent`, not `zip`.
- Notarize with `notarytool`, and staple **both** the `.app` and the dmg.
- Verify local-network discovery by launching the `.app` **from Finder**. Running the binary from a
  terminal is auto-allowed and gives a false pass. Test on a snapshot.

`EnableCompressionInSingleFile` must stay off — it causes bus errors on Apple Silicon — and the
executable in `Contents/MacOS/` must remain a real file, which CI already asserts.

---

## 4. Hardware verification

**Status: needs machines this was not built on. The instrumentation ships; the numbers do not.**

Everything below is implemented and reviewable. None of it is *verified*, and none of the
corresponding boxes should be ticked by reading code. The diagnostics view exists precisely to make
the first group measurable.

**Linux is no longer wholly unrun.** The backend has been executed on Fedora 44 / PipeWire 1.6.8 /
OpenAL Soft 1.24.2 against Music Assistant: audio, artwork and metadata all confirmed audible and
visible by a human, `audio-clock` timing, 21 ms measured device latency, device switching, volume and
mute. What that run did **not** cover is the sync-quality group immediately below — no second client
was playing alongside it, so the ±1 ms figure remains unmeasured on every platform. Getting audio out
of one machine is a much weaker claim than sync, and the two should not be confused.

That first run also found the bug that had made *every* platform silent against a current server —
unversioned `supported_roles` — which is recorded in `docs/COMPLIANCE.md`. It is the reason this item
existing as "unverified" was expensive: the defect was in shared code, and no amount of reading it
would have surfaced it.

### Sync quality — the one that matters most

Measure against a second Sendspin client on the same server, on each of Windows, macOS and Linux:

- Steady-state sync error within **±1 ms** (target ±0.5 ms).
- Speed deviation within **±0.5 %** over a 150 ms sliding window.
- Corrections inaudible: no warble at startup, no artefact on correction.

Capture the numbers from the diagnostics view and record them. **Check the timing source field
first** — anything other than `audio-clock` means that platform's backend is not supplying a
hardware clock and every other figure is resting on the OS timer instead.

### Per-platform integration

| Platform | What to verify |
|---|---|
| Windows 11 | Media flyout shows title/artist/artwork; hardware media keys drive transport; works from an unpackaged build; the taskbar overlay badge appears |
| Plasma 6 | App appears in the media applet with artwork; buttons work; media keys work **through MPRIS alone**; `busctl --user introspect org.mpris.MediaPlayer2.io.sendspin.client` shows the interface; tray icon appears |
| GNOME | Same, plus the AppIndicator extension situation — including the extension's bus name vanishing on a shell restart |
| macOS | Now Playing / Control Center metadata and artwork; media keys. Needs item 3 first |
| Both shells | Album art loads from the `file://` path; fractional HiDPI at 125 % and 150 %; MPRIS and tray work **inside** the Flatpak, not only outside it |

The MPRIS implementation is hand-written D-Bus dispatch. It has now **been executed** — it publishes
cleanly as `org.mpris.MediaPlayer2.io.sendspin.client` on the session bus at every start, alongside
the tray icon — but only its publication is confirmed. The applet integration in the table above,
and the media-key behaviour, are still unverified. Continue with `dbus-monitor` and
`busctl --user introspect`.

---

## 5. Surface the rest of the 9.3 buffer statistics

**Status: blocked on the SDK's published surface, not on effort.**

9.3 added the statistics that explain a bad sync number rather than restate it, and the diagnostics
view can show only one of them. `AudioBufferStats.ClockDriftMs` is public and is displayed.
`HardSyncStalled`, `HardSyncCount`, `LateChunksDropped` and `ContentHolesDetected` are **documented
in the SDK's XML but declared `internal`**, because the 9.x line freezes its published surface.

The loss that matters is `HardSyncStalled`. The SDK's own docs call it the actionable one: true means
splicing has stopped closing the error, and the usual cause is an output latency the platform reports
wrongly — which is exactly what this app's per-device manual offset exists to correct. Without it a
user with a mis-reported latency sees a bad number and no hint of what to do about it.

Reading them by reflection was considered and rejected: a shipping diagnostics path that depends on
another package's private members fails silently and invisibly the first time a name moves.

**First action:** when the pin moves to 10.x (item 1), check whether these are public there and add
them to `PlayerDiagnosticsSnapshot` and `StatsWindow.axaml`. If 10.x also keeps them internal,
that is worth an upstream issue alongside item 2.

---

## 6. Wire the conformance harness into CI

**Status: named rather than quietly dropped.**

`Sendspin/conformance` is a separate repository and its adapter and invocation contract were not
available to this change, so wiring it would have meant guessing at an interface. The unit suite is
**not** a substitute: it tests our decisions, not our protocol conformance.

**First action:** find the harness's `sendspin-dotnet` adapter, work out how it launches a client
under test, and add a CI job. Any scenario deliberately skipped must be **logged by name and
reason** — silent narrowing is worse than no harness.

---

## 7. One loose thread

On the very first run of the built app, the settings file was written with
`notifications.track_change: true` and `playback_state: true`, contradicting the shipped defaults
(both false) and the requirement that per-track notifications default off.

It has **not** reproduced. A second run from a deleted config wrote the correct values, the
in-memory defaults and the JSON round-trip are both provably correct, and a test asserts the
default. At the time, `SettingsService.Current` published a live mutable object that several threads
could mutate while it was being serialized — the most plausible cause, and now fixed by
copy-on-write with tests.

**First action:** nothing, unless it recurs. If it does, look at the two-way `CheckBox` bindings in
`SettingsView.axaml` writing back during control realisation, which is the remaining candidate and
is not reachable from the cross-platform test project.

---

## 8. Every `DispatcherTimer` is quantised on the Wayland head

**Status: resolved in reskin phase 2 by `UiClock` and elapsed-time position. Re-check on every
`Avalonia.Wayland` bump.**

On the default Linux head `Avalonia.Wayland` 12.1.1 fires `DispatcherTimer` ticks on a coarse quantum
of 100–140 ms: a 16 ms timer ticks at the quantum, a 100 ms one on time with an occasional 200 ms
gap, and a 500 ms one on time or one quantum late depending on the run — `618, 501, 618, 500, …` in
one per-gap run, on time (max 503 ms) in three others. It is not "every 600 ms", which was a counting
artifact, and it is not reliably on time either; both sets of measurements are in the *UI shell*
section of `docs/ARCHITECTURE.md`. The same backend runs `RequestAnimationFrame` and Avalonia's own
animation clock at about 40 000 Hz without a frame between, so neither is a substitute.

**Done:** `src/Sendspin.Player/Threading/UiClock.cs` — a `System.Threading.Timer` posting one tick to
`Dispatcher.UIThread` at `Render` priority (62 Hz on Wayland, X11 and macOS), dropping a tick while the
previous one is still queued. Both view-model timers use it, and a hygiene test in `Sendspin.Ui.Tests`
keeps `DispatcherTimer` out of every other file in the app. `MainViewModel` advances the position by
the measured time since the server's last report (`Core/MediaSession/AnchoredPosition.cs`) rather than
by the nominal interval, so the bar is right whatever the timer does. Phase 5 paces the backdrop with
the same helper at 60 Hz.

**Re-check** the backend on every `Avalonia.Wayland` bump before deciding whether the helper can go
back to a `DispatcherTimer`:

```
dotnet run --project scripts/spike/ShellSpike -- clock
```

---

## 9. The artwork handler ignores the frame's channel and display timestamp

`SendspinPlayerService.OnArtworkReceived` and `OnArtworkCleared` read neither `Channel` nor
`Timestamp` off the artwork frame. Harmless today: `client/hello` advertises exactly one artwork
channel, so every frame is channel 0, and the server-clock timestamp — when the picture *should be
shown* — is ignored in favour of showing it on arrival. Naming the cached file by its bytes fixed
the stale-art race that ordering produced, so the residue is at worst a sub-second early cover
when the next track's picture lands ahead of its metadata.

**First action:** none until a second channel (artist art) is advertised; a clear on channel 1 would
then blank the album art. Honouring the timestamp means holding the publish until clock sync says
server time has reached it, and is its own change — it does not replace per-picture paths, which are
still needed at the boundary.

---

## 10. Double-click the macOS toolbar to zoom

The macOS window is movable by its toolbar (`MainWindow.OnToolbarPointerPressed`, and the
drag-handle rule in `ARCHITECTURE.md`'s "As shipped (reskin phase 2)"). Double-clicking a title bar
to zoom, the other half of what a native Mac title bar does, was asked for at the same time and
deliberately left out.

macOS has a system preference for what a title-bar double-click does — zoom, minimise, or nothing
(`AppleActionOnDoubleClick`) — and Avalonia 12 exposes no way to read it. Setting
`WindowState = Maximized` on a double-click would hardcode one of those three answers and put an
unmeasured platform assumption into the one section of `ARCHITECTURE.md` where every other claim is
a measurement.

**First action:** a `ShellSpike chrome` case that sets `WindowState = Maximized` on macOS and
records what actually happens to the frame, then the behaviour behind an "As shipped" paragraph
that says so. Reading the preference itself needs a P/Invoke to `CFPreferencesCopyAppValue`, which
is only worth it if the spike shows `Maximized` is the wrong answer for two of the three settings.

---

## Things that are done and should not be reopened

Recorded so the reasoning is not relitigated from scratch:

- **Not adopting `sendspin-cpp`.** C++-only static library, zero `extern "C"`, no `SHARED` target,
  no `install()`/`export()` rules — P/Invoke would need a permanently-maintained C shim. Its host
  build is macOS/Linux only, and it is behind the spec.
- **Not consolidating onto one cross-platform audio library.** None of miniaudio, SDL3, RtAudio,
  libsoundio, cubeb or PortAudio gives a live per-callback DAC timestamp on all three platforms, and
  several have no PipeWire backend at all — adopting one would actively regress Linux.
- **Avalonia stays, and Wayland is the Linux default.** It was X11, on the reasoning that the
  native Wayland backend was experimental and bought little: `UsePlatformDetect()` never selects
  it, and every desktop integration here is D-Bus and therefore identical either way. What that
  reasoning missed is that Wayland buys correct fractional HiDPI, and that the two protocols the
  backend does not bind are already routed around — idling through the Inhibit portal, raising the
  window through the notification daemon's activation token. The backend is still experimental, so
  `SENDSPIN_X11=1` gets back to X11 without a rebuild, and a machine with no Wayland session gets
  X11 by decision rather than by accident.
- **iOS is out of scope, and when it comes it is not Avalonia.** `Sendspin/SendspinKit` is a
  complete first-party Swift SDK; SwiftUI plus SendspinKit is the answer there.
- **No native C audio shim yet.** See `docs/ARCHITECTURE.md` for the per-platform realtime story and
  where the macOS one is knowingly short.
- **The native shell.** The window follows the system theme, accent and font, with the OS's own
  decorations; the composition — the layer stack, Now Playing's two layouts, the settings card, the
  Stats window, the living backdrop — comes from Sendspin for Windows, and the colours do not. Every
  colour is a theme or accent token; the hygiene test in `Sendspin.Ui.Tests` keeps literal colours
  out of the axaml. The reasoning and the measurements are the *UI shell* section of
  `docs/ARCHITECTURE.md`.
- **The `UiClock` rule.** No `DispatcherTimer`, `RequestAnimationFrame` or Avalonia `Animation` in
  the app: the Wayland backend quantises the first and runs the other two at tens of kilohertz with
  no frame between (item 8). Everything periodic goes through `Player/Threading/UiClock.cs`, and a
  hygiene test keeps `DispatcherTimer` out of every other file. Re-measure the backend on every
  `Avalonia.Wayland` bump before relaxing this; do not relax it on the strength of a reading.
- **The two backdrop roles are advertised.** `client/hello` lists `color@v1` and `visualizer@v1`
  with a `visualizer@v1_support` object, verified live against Music Assistant; the roles and the
  support object are one pin. Taking them out again to slim the hello would put the living backdrop
  back on the theme accent alone — the fallback exists for a server that lacks the roles, not as a
  mode to prefer.
