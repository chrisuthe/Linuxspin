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
implement Pairing PSK, and requires the management role of all clients. `Sendspin.SDK` 9.1.0 ships
none of the three. That work is merged on `sendspin-dotnet` `main` after 9.1.0 (PRs #67 and #73) but
is not in a published package.

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

---

## 2. File the upstream convergence-gating issue

**Status: needs a human. An agent may not open issues (`AI_POLICY.md`).**

The spec requires a player to withhold availability until its time filter has converged, with no
"timed out, proceed anyway" path. This **cannot be satisfied from this repository** against SDK
9.1.0. The pipeline is constructed with `waitForConvergence: true`, but on timeout the SDK logs

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

The MPRIS implementation is hand-written D-Bus dispatch that has **never been executed** — no Linux
machine was available. It was reviewed against the specification line by line and the types check
out, but first-run behaviour is genuinely unknown. Start with `dbus-monitor` and
`busctl --user introspect`.

---

## 5. Wire the conformance harness into CI

**Status: named rather than quietly dropped.**

`Sendspin/conformance` is a separate repository and its adapter and invocation contract were not
available to this change, so wiring it would have meant guessing at an interface. The unit suite is
**not** a substitute: it tests our decisions, not our protocol conformance.

**First action:** find the harness's `sendspin-dotnet` adapter, work out how it launches a client
under test, and add a CI job. Any scenario deliberately skipped must be **logged by name and
reason** — silent narrowing is worse than no harness.

---

## 6. One loose thread

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

## Things that are done and should not be reopened

Recorded so the reasoning is not relitigated from scratch:

- **Not adopting `sendspin-cpp`.** C++-only static library, zero `extern "C"`, no `SHARED` target,
  no `install()`/`export()` rules — P/Invoke would need a permanently-maintained C shim. Its host
  build is macOS/Linux only, and it is behind the spec.
- **Not consolidating onto one cross-platform audio library.** None of miniaudio, SDL3, RtAudio,
  libsoundio, cubeb or PortAudio gives a live per-callback DAC timestamp on all three platforms, and
  several have no PipeWire backend at all — adopting one would actively regress Linux.
- **Avalonia stays, and X11 stays the default.** The native Wayland backend is real and available
  behind `SENDSPIN_WAYLAND=1`, but it is experimental, `UsePlatformDetect()` never selects it, and
  every desktop integration here is D-Bus and therefore identical either way.
- **iOS is out of scope, and when it comes it is not Avalonia.** `Sendspin/SendspinKit` is a
  complete first-party Swift SDK; SwiftUI plus SendspinKit is the answer there.
- **No native C audio shim yet.** See `docs/ARCHITECTURE.md` for the per-platform realtime story and
  where the macOS one is knowingly short.
