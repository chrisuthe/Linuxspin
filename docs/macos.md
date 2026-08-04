# macOS build readiness

An assessment of what the Sendspin Player codebase does on macOS today. It is a
report, not a change: nothing in the build, the projects or the source was
modified to produce it. Every command quoted below was run on the machine
described in [Test environment](#test-environment), and every block of output is
that run's real output.

> **Point-in-time.** This assesses commit `e0300dc`. The `file:line` references
> throughout were correct against that commit and will drift as the code changes
> — in particular, follow-ups [(a)](#a-drop-the-unused-vulnerable-tmdsdbusprotocol-reference)
> and [(d)](#d-remove-the-duplicate-unreferenced-implementations) delete lines
> this document cites. Treat the citations as a guide to *where* to look, not as
> live coordinates.

The work this assessment identifies is collected in
[Follow-up tasks](#follow-up-tasks).

---

## 1. Verdict

**Yes — the solution compiles on macOS arm64, and the application also runs.**
There are zero macOS-specific compile errors and zero warnings. A self-contained
`osx-arm64` publish succeeds, and the resulting Mach-O binary launches and
registers with the macOS window server as a foreground GUI application.

Two qualifications, and the difference between them matters:

- **`dotnet build Sendspin.Player.sln` does fail** — but not on macOS grounds. It
  fails during *restore*, before a single file is compiled, on a NuGet
  vulnerability audit for `Tmds.DBus.Protocol` that `TreatWarningsAsErrors`
  promotes to an error. This break is platform-independent: it fails on Linux and
  Windows CI in exactly the same way. See [section 3](#3-what-blocks-a-clean-dotnet-build).
- **Compiling and running is not the same as being supported.** macOS has no
  platform implementation in this repo. A compile-time `#if WINDOWS`/`#else`
  routes it to `LinuxPlatformInitializer`, so what runs on a Mac is the Linux
  build. More of it works than that suggests, but where it is wrong it fails
  silently rather than loudly. See
  [section 5](#5-runtime-gaps-compiles--behaves-correctly).

There is no macOS target anywhere in the build, packaging or release plumbing.
See [section 6](#6-build--release-plumbing-gaps).

### Test environment

| | |
|---|---|
| OS | macOS 26.6 (build 25G72), `Darwin 25.6.0 arm64` |
| SDK | `dotnet` 10.0.302 |
| Runtimes installed | `Microsoft.NETCore.App` 10.0.10, `Microsoft.AspNetCore.App` 10.0.10 |
| .NET 8 runtime | **not installed** — see [section 7](#7-tests-cannot-run-and-there-are-none) |
| Commit | `e0300dc` (`master`) |

Builds below use `--artifacts-path` to keep `bin/`/`obj/` out of the working
tree; the paths in the output are that scratch location, not a repo path.

---

## 2. How to reproduce

```bash
# 1. The plain solution build — fails at restore (not a macOS failure)
dotnet build Sendspin.Player.sln

# 2. The same build with the audit bypassed — succeeds
dotnet build Sendspin.Player.sln -p:NuGetAudit=false

# 3. The Windows TFM, requested explicitly — fails, correctly
dotnet build src/Sendspin.Player/Sendspin.Player.csproj \
  -f net8.0-windows10.0.19041.0 -p:NuGetAudit=false

# 4. Tests — cannot run
dotnet test src/Sendspin.Player.Tests/Sendspin.Player.Tests.csproj -p:NuGetAudit=false

# 5. A self-contained macOS publish — succeeds, and the result runs
dotnet publish src/Sendspin.Player/Sendspin.Player.csproj \
  -f net8.0 -r osx-arm64 --self-contained -p:NuGetAudit=false

# 6. Launch it. Point HOME at a scratch dir to watch where it writes config
#    without polluting your real home directory.
cd <publish-output>
HOME=/tmp/sendspin-scratch ./Sendspin.Player

# 7. The Makefile has no macOS target, but honours the RID we pass it
make publish RUNTIME=osx-arm64
```

Add `--artifacts-path <dir>` to the `dotnet` commands to keep `bin/`/`obj/` out
of the source tree, as was done here. `make publish` writes to the gitignored
`artifacts/` directory but builds into `src/**/bin`.

---

## 3. What blocks a clean `dotnet build`

```
$ dotnet build Sendspin.Player.sln
  Determining projects to restore...
src/Sendspin.Player.Services/Sendspin.Player.Services.csproj : error NU1903:
  Warning As Error: Package 'Tmds.DBus.Protocol' 0.20.0 has a known high
  severity vulnerability, https://github.com/advisories/GHSA-xrw6-gwf8-vvr9
  Failed to restore src/Sendspin.Player.Services/Sendspin.Player.Services.csproj

Build FAILED.
    0 Warning(s)
    1 Error(s)
```

Two settings combine to make this fatal:

- `Directory.Build.props:6` — `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`,
  which promotes the `NU1903` audit *warning* into a build-stopping error.
- `src/Sendspin.Player.Services/Sendspin.Player.Services.csproj:22` —
  `<PackageReference Include="Tmds.DBus.Protocol" Version="0.20.0" />`.

**The package is referenced but never used.** A search of the entire source tree
returns nothing:

```
$ grep -rn "Tmds" --include="*.cs" src/; echo "exit: $?"
exit: 1        # no output, no matches
```

There is no D-Bus code in the repo at all. The one D-Bus-adjacent feature,
desktop notifications, shells out to `notify-send` rather than talking to the
bus (`src/Sendspin.Platform.Linux/Notifications/LinuxNotificationService.cs:115`).

This is the single highest-value fix surfaced by this assessment, and it is not a
macOS fix: **deleting one line unblocks restore on all three platforms and
removes a high-severity advisory from the dependency graph.** See follow-up
task [(a)](#a-drop-the-unused-vulnerable-tmdsdbusprotocol-reference).

---

## 4. What builds and what doesn't

With the audit bypassed, the whole solution compiles cleanly:

```
$ dotnet build Sendspin.Player.sln -p:NuGetAudit=false
  Sendspin.Core -> .../bin/Sendspin.Core/debug/Sendspin.Core.dll
  Sendspin.Platform.Shared -> .../bin/Sendspin.Platform.Shared/debug/Sendspin.Platform.Shared.dll
  Sendspin.Player.Services -> .../bin/Sendspin.Player.Services/debug/Sendspin.Player.Services.dll
  Sendspin.Platform.Windows -> .../bin/Sendspin.Platform.Windows/debug/Sendspin.Platform.Windows.dll
  Sendspin.Platform.Linux -> .../bin/Sendspin.Platform.Linux/debug/Sendspin.Platform.Linux.dll
  Sendspin.Player -> .../bin/Sendspin.Player/debug_net8.0/Sendspin.Player.dll
  Sendspin.Player.Tests -> .../bin/Sendspin.Player.Tests/debug/Sendspin.Player.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

| Project | TFM | On macOS arm64 |
|---|---|---|
| `Sendspin.Core` | `net8.0` | builds |
| `Sendspin.Platform.Shared` | `net8.0` | builds |
| `Sendspin.Player.Services` | `net8.0` | builds |
| `Sendspin.Platform.Linux` | `net8.0` | builds |
| `Sendspin.Platform.Windows` | `net8.0-windows10.0.19041.0` | **builds** — via reference packs, see below |
| `Sendspin.Player` | `net8.0` | builds |
| `Sendspin.Player` | `net8.0-windows10.0.19041.0` | **not built** by the solution build; fails if requested |
| `Sendspin.Player.Tests` | `net8.0` | builds (but cannot run) |

### Why `Sendspin.Platform.Windows` builds on a Mac

`src/Sendspin.Platform.Windows/Sendspin.Platform.Windows.csproj:12` sets
`<EnableWindowsTargeting>true</EnableWindowsTargeting>`. That lets the SDK
satisfy the Windows framework references from *reference* packs, which are
platform-neutral, so the compile succeeds anywhere. The resulting assembly is
still Windows-only at runtime — reference packs are enough to compile against,
not to run on.

### The one target that does not build

`src/Sendspin.Player/Sendspin.Player.csproj:5` declares
`<TargetFrameworks>net8.0;net8.0-windows10.0.19041.0</TargetFrameworks>`. Asked
for the Windows TFM explicitly, the build fails:

```
$ dotnet build src/Sendspin.Player/Sendspin.Player.csproj -f net8.0-windows10.0.19041.0 -p:NuGetAudit=false
.../Microsoft.NET.Sdk.FrameworkReferenceResolution.targets(544,5): error NETSDK1082:
  There was no runtime pack for Microsoft.WindowsDesktop.App available for the
  specified RuntimeIdentifier 'osx-arm64'.
.../Microsoft.NET.Sdk.FrameworkReferenceResolution.targets(544,5): error NETSDK1082:
  There was no runtime pack for Microsoft.WindowsDesktop.App.WindowsForms available
  for the specified RuntimeIdentifier 'osx-arm64'.

Build FAILED.
    0 Warning(s)
    2 Error(s)
```

Nothing in the repo asks for WPF or WinForms — there is no `UseWPF` or
`UseWindowsForms` anywhere in `src/`. Both framework references arrive
transitively from packages, and each maps to one of the two errors above.
Asking MSBuild directly (`-getItem:FrameworkReference` against the restored
assets for that TFM) gives:

| Framework reference | Declared by | Which arrives via |
|---|---|---|
| `Microsoft.WindowsDesktop.App.WindowsForms` | `NAudio.WinForms` 2.2.1 | `NAudio` 2.2.1 (`src/Sendspin.Platform.Windows/Sendspin.Platform.Windows.csproj:17`), reached through the Windows-only `ProjectReference` at `src/Sendspin.Player/Sendspin.Player.csproj:70` |
| `Microsoft.WindowsDesktop.App` | `System.Reactive` 5.0.0 | `Zeroconf` 3.7.16 → `Sendspin.SDK` 6.3.6 |

So why does `Sendspin.Platform.Windows` — which references the same `NAudio` —
build fine on a Mac while `Sendspin.Player` does not? Because it is a **library**,
and a library only needs *reference* packs to compile, exactly as described
above. `Sendspin.Player` is an **application**
(`<OutputType>WinExe</OutputType>`, `src/Sendspin.Player/Sendspin.Player.csproj:4`),
and an app must resolve *runtime* packs for its target RID. There is no
`Microsoft.WindowsDesktop.App` runtime pack for `osx-arm64`, so it fails.

The Windows-conditional property group at
`src/Sendspin.Player/Sendspin.Player.csproj:26-28` is **not** the cause — it only
defines the `WINDOWS` preprocessor symbol that drives the `#if` in
[section 5](#macos-silently-takes-the-linux-path).

**This does not affect anyone building normally.** Both the solution build and a
bare `dotnet build` of the project emit only `debug_net8.0/` and report
`0 Error(s)`; the Windows inner build does not run on macOS. A Mac developer only
sees `NETSDK1082` by asking for `-f net8.0-windows10.0.19041.0` by hand.

**But do not rely on that skip.** It is worth being precise about how little is
understood here, because the obvious explanation is wrong:

- `TargetFrameworks` really does list both TFMs, and `EnableWindowsTargeting` is
  `true` (`src/Sendspin.Player/Sendspin.Player.csproj:20`) — both confirmed via
  `dotnet msbuild -getProperty`. So the skip is *not* "Windows targeting is
  turned off"; that switch exists precisely to enable Windows TFMs off-Windows,
  and it is on.
- The decision is made inside the SDK's compiled framework-reference resolution,
  not by anything in this repo, so it is not a behaviour the project has pinned
  or can see.
- `PublishSingleFile` is set as a project-wide property
  (`src/Sendspin.Player/Sendspin.Player.csproj:22`) rather than passed at publish
  time. That is a smell worth a look on its own: single-file publishing normally
  belongs on the publish command, and project-wide RID-sensitive properties are
  one way an unwanted RID resolution creeps into a plain `build`.

The practical outcome on macOS today is correct, so nothing needs fixing *now* —
but a silent skip that the repo does not control is a thing that can start
failing on an SDK update. Running this down is part of follow-up task
[(c)](#c-real-macos-platform-support).

---

## 5. Runtime gaps: compiles ≠ behaves correctly

### macOS silently takes the Linux path

`src/Sendspin.Player/App.axaml.cs:12-16` picks the platform namespace at compile
time:

```csharp
#if WINDOWS
using Sendspin.Platform.Windows.Platform;
#else
using Sendspin.Platform.Linux.Platform;
#endif
```

and `src/Sendspin.Player/App.axaml.cs:101-105` picks the initializer the same
way:

```csharp
// Select platform-specific initializer based on compile-time target
#if WINDOWS
IPlatformInitializer platformInitializer = new WindowsPlatformInitializer();
#else
IPlatformInitializer platformInitializer = new LinuxPlatformInitializer();
#endif
```

There are exactly two branches and macOS is not one of them, so **macOS falls
through to `LinuxPlatformInitializer`**. There is no error, no warning, and no
log line saying so.

> **The `ConfigureServices` docstring is wrong.**
> `src/Sendspin.Player/App.axaml.cs:84-88` states: *"The platform is detected at
> runtime and the appropriate initializer is used."* No runtime detection
> happens. The split is entirely compile-time, and the inline comment at
> `src/Sendspin.Player/App.axaml.cs:100` — *"Select platform-specific initializer
> based on compile-time target"* — contradicts the docstring directly.
> Correcting it is part of follow-up task
> [(c)](#c-real-macos-platform-support).

That means every service in
`src/Sendspin.Platform.Linux/Platform/LinuxPlatformInitializer.cs:20-36` — paths,
audio, notifications, Discord presence — is the Linux implementation.

### It really does run

This was verified directly rather than reasoned about. A self-contained publish
produces a native binary:

```
$ dotnet publish src/Sendspin.Player/Sendspin.Player.csproj \
    -f net8.0 -r osx-arm64 --self-contained -p:NuGetAudit=false
  Sendspin.Player -> .../publish/Sendspin.Player/release_net8.0_osx-arm64/

$ file Sendspin.Player
Sendspin.Player: Mach-O 64-bit executable arm64
```

Launched with `HOME` pointed at a scratch directory, it starts and initializes:

```
info: Sendspin.Player.App[0]
      Sendspin Linux client initialized
dbug: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      Created directory: <HOME>/.config/sendspin
dbug: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      Created directory: <HOME>/.local/share/sendspin
dbug: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      Created directory: <HOME>/.cache/sendspin
dbug: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      Created directory: <HOME>/.local/share/sendspin/logs
dbug: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      Created directory: <HOME>/.cache/sendspin/album-art
info: Sendspin.Platform.Linux.Platform.LinuxPaths[0]
      XDG directories initialized
```

Those `LinuxPaths` log sources are the fall-through described above, observed in
practice. The process is a real GUI application, not a headless stub —
`lsappinfo` reports it checked in with the window server:

```
$ lsappinfo list | grep -A2 -i sendspin
    executable path=".../release_net8.0_osx-arm64/Sendspin.Player"
    pid = 36301 type="Foreground" flavor=3 Arch=ARM64
```

Avalonia handles this on its own: `Avalonia.Native.dll` ships in the publish
output and the macOS backend is selected without any code in this repo asking
for it. It ran to a manual kill with no exceptions logged.

### The four consequences

| Area | On macOS | Evidence |
|---|---|---|
| **Audio** | **Works** (device opens) | verified — see below |
| **Resampling** | **Degrades** to lower quality, and hides a swallowed exception doing it | verified — `libspeexdsp` absent; bare `catch {}` |
| **Notifications** | **Disabled**, silently | verified — `notify-send` absent |
| **Paths** | **Wrong location**, but functional | verified — XDG dirs created, above |

**Audio — works.** `OpenALAudioPlayer` opens the default device via Silk.NET at
`src/Sendspin.Platform.Linux/Audio/OpenALAudioPlayer.cs:84`
(`_alc.OpenDevice((string?)null)`). The `Silk.NET.OpenAL.Soft.Native` 1.23.1
package ships `osx-arm64/native/libopenal.dylib` and `osx-x64/`, and the build
copies the right one into the output. Calling the same entry points that
`OpenALAudioPlayer` calls, against the `libopenal.dylib` from the publish output,
succeeds:

```
alcOpenDevice(NULL) -> 0xbe0908000
default device      : OpenAL Soft
alcCreateContext    -> 0xbe126c000
alcMakeContextCurrent -> True
```

*Not verified:* that audio actually **plays** — that a stream decodes, buffers
queue, and sound reaches the speakers in sync. That needs a live Sendspin server
and a listening human. What is established is that the native library loads and
the device and context open on macOS arm64, which is where a broken port would
fail first.

**Resampling — degrades, and hides it.** `LinuxSpeexResampler` P/Invokes
`libspeexdsp.so.1` through its `SpeexNative` helper class
(`src/Sendspin.Platform.Linux/Audio/LinuxSpeexResampler.cs:118-120`), a Linux
`.so` name that does not resolve on macOS; no `libspeexdsp` is present on the
test machine in `/usr/lib` or `/opt/homebrew/lib`. macOS therefore always gets
`LinearInterpolationResampler`, at a real cost in resampling quality.

It does not crash, and the `IsAvailable` guard in `DynamicResamplerFactory.Create`
(`src/Sendspin.Platform.Linux/Audio/DynamicResamplerFactory.cs:14-25`) is the
legitimate part of that. **The `try` around it is not** — it is a bare
`catch { }` whose entire body is a comment:

```csharp
catch
{
    // Fall through to fallback
}
```

That swallows *every* exception from constructing the native resampler — a
genuine native-library or argument fault included — and substitutes a
lower-quality resampler with no log line at any level. The duplicate copy at
`src/Sendspin.Player.Services/Audio/SpeexResampler.cs:64` is worse still: a
completely empty `catch { }`.

This is a defect in its own right, and a platform-independent one — on Linux it
would equally mask a broken `libspeexdsp`. It is not a macOS finding, but macOS
is where it bites every single run. See follow-up task
[(f)](#f-stop-silently-swallowing-resampler-construction-failures).

**Notifications — disabled safely.** `LinuxNotificationService` probes for
`notify-send` with `which` at
`src/Sendspin.Platform.Linux/Notifications/LinuxNotificationService.cs:29-46`,
logs `"notify-send not available - notifications disabled"` at warning level, and
disables itself. `notify-send` is not present on macOS. Track and connection
notifications simply never appear; macOS has `UNUserNotificationCenter` and
nothing here uses it.

**Paths — wrong but functional.** `LinuxPaths` returns XDG locations —
`~/.config/sendspin`, `~/.local/share/sendspin`, `~/.cache/sendspin`
(`src/Sendspin.Platform.Linux/Platform/LinuxPaths.cs:31-37`) — where a Mac
application should use `~/Library/Application Support`, `~/Library/Caches` and
`~/Library/Logs`. Confirmed in the launch above: the app created the XDG tree.
Nothing breaks; the files are just in un-Mac-like places, invisible in Finder and
missed by migration and backup tooling that knows about `~/Library`.

### Dead code found along the way

Neither of these affects macOS behaviour, but both were surfaced by tracing the
above and both are cheap to remove:

- `src/Sendspin.Player/Configuration/AppPaths.cs` is a second XDG path
  implementation duplicating `LinuxPaths`. `grep -rn "AppPaths" --include="*.cs" src/`
  matches only inside that file — nothing constructs or injects it.
- `src/Sendspin.Player.Services/Audio/SpeexResampler.cs` is a second SpeexDSP
  binding, with its own `libspeexdsp.so.1` `LibraryImport` block at
  `src/Sendspin.Player.Services/Audio/SpeexResampler.cs:180-210` and its own
  `IsAvailable` guard. Nothing outside that file references it either — the
  factory uses `LinuxSpeexResampler`.

See follow-up task [(d)](#d-remove-the-duplicate-unreferenced-implementations).

---

## 6. Build & release plumbing gaps

There is **no macOS target anywhere**.

- **`Makefile:31`** — `RUNTIME ?= linux-x64`. `make publish-all` (`Makefile:187-188`)
  covers `linux-x64` and `linux-arm64` only; no macOS RID appears anywhere in the
  file. The `publish` target does pass `RUNTIME` straight through to
  `dotnet publish --runtime`, so a macOS build works *by accident* —
  `make publish RUNTIME=osx-arm64` was run for this assessment and produced a
  working 48 MB single-file `Mach-O 64-bit executable arm64` in
  `artifacts/osx-arm64/`. Nothing declares it, documents it, or builds it in CI.
- **`.github/workflows/build.yml`** — the only workflow. Its jobs are
  `build-linux` (`:20`, `runs-on: ubuntu-latest`), `package-flatpak` (`:61`),
  `package-appimage` (`:132`), `build-windows` (`:190`, `runs-on: windows-latest`)
  and `package-windows` (`:250`). There is no `macos-latest` runner, no
  `osx-arm64`/`osx-x64` publish and no macOS artifact.
- **No `.app` bundle.** The publish output is a bare executable in a flat
  directory. There is no `Info.plist`, no `Sendspin.app` layout, no icon set
  (`.icns`), no code signing and no notarization — so it is not something a Mac
  user could double-click from `/Applications`. Note the distinction: a build you
  produce locally runs fine unsigned, as [section 5](#it-really-does-run)
  demonstrates. Gatekeeper's quarantine applies to a *downloaded* build, which is
  what signing and notarization exist to satisfy.
- **Packaging is Linux-only.** `packaging/` holds `flatpak/`, `appimage/` and
  `deb/` and nothing else.
- **README is Linux- and Windows-only.** It describes the project as "supporting
  Windows and Linux" (`README.md:3`), and its installation, development-workflow
  and deployment sections cover those two platforms. The requirements list
  (`README.md:11-16`) carries a single "not a supported target" line pointing
  here.

---

## 7. Tests cannot run, and there are none

```
$ dotnet test src/Sendspin.Player.Tests/Sendspin.Player.Tests.csproj -p:NuGetAudit=false
Test run for .../Sendspin.Player.Tests.dll (.NETCoreApp,Version=v8.0)
A total of 1 test files matched the specified pattern.
Testhost process for source(s) '.../Sendspin.Player.Tests.dll' exited with error:
You must install or update .NET to run this application.
Architecture: arm64
Framework: 'Microsoft.NETCore.App', version '8.0.0' (arm64)
.NET location: /Users/chris/.dotnet/

The following frameworks were found:
  10.0.10 at [/Users/chris/.dotnet/shared/Microsoft.NETCore.App]

Test Run Aborted.
```

The test host targets `net8.0` and only the .NET 10 runtime is installed. This is
a toolchain mismatch, not a macOS problem — the same would happen on Linux with
only .NET 10 present, and it does not happen on any machine that has a .NET 8
runtime. The cheapest fix is to install the .NET 8 runtime; follow-up task
[(b)](#b-retarget-net80--net100) also removes it, as a side effect rather than as
its justification.

Independently of that, **the test project contains no tests**:

```
$ find src/Sendspin.Player.Tests -type f | grep -v -E '/(bin|obj)/'
src/Sendspin.Player.Tests/Sendspin.Player.Tests.csproj
```

Only the `.csproj`. Not a single `.cs` file. Any "tests green" gate on this repo
is currently vacuous, on every platform. See follow-up task
[(e)](#e-give-sendspinplayertests-some-tests).

---

## 8. Decisions and follow-up tasks

### Recorded decision: retarget `net8.0` → `net10.0`

**Agreed during drafting.** Recorded here as the decision; the reasoning below is
stated carefully because the two candidate justifications are not equally good.

*What prompted it* was that this machine has only the .NET 10 SDK and runtime, so
tests will not run. **That alone does not justify a solution-wide retarget** —
"one workstation lacks a runtime" is fixed on the workstation, by installing the
.NET 8 runtime, or with a `global.json`. A framework change touches every
packaged artifact on every platform; the trigger and the justification should not
be confused.

*What does justify it* is support lifecycle. .NET 8 and .NET 10 are both LTS; 10
is current, and staying on 8 means a migration later regardless. Doing it now,
while the repo is small and has no tests to break, is cheaper than doing it after
macOS support and a test suite have been layered on top. That is the argument
worth putting in the PR.

**One consequence to accept deliberately.** Commit `e0300dc` added a
framework-dependent Windows artifact (`.github/workflows/build.yml:225-234`,
published as `sendspin-player-<rid>-fdd`). Retargeting means every consumer of
that artifact needs the .NET 10 desktop runtime installed, which is far less
widely present today than .NET 8. Either accept the narrower audience, or drop
the framework-dependent artifact and ship self-contained only.

This is **not** a macOS issue, and macOS work is not blocked on it.

The full surface, verified by sweeping the repo rather than taken from the
drafting notes:

| File | Change |
|---|---|
| `Directory.Build.props:3` | `net8.0` → `net10.0` |
| `src/Sendspin.Core/Sendspin.Core.csproj:4` | `net8.0` → `net10.0` |
| `src/Sendspin.Platform.Shared/Sendspin.Platform.Shared.csproj:4` | `net8.0` → `net10.0` |
| `src/Sendspin.Platform.Linux/Sendspin.Platform.Linux.csproj:4` | `net8.0` → `net10.0` |
| `src/Sendspin.Player.Services/Sendspin.Player.Services.csproj:4` | `net8.0` → `net10.0` |
| `src/Sendspin.Player.Tests/Sendspin.Player.Tests.csproj:4` | `net8.0` → `net10.0` |
| `src/Sendspin.Platform.Windows/Sendspin.Platform.Windows.csproj:4` | `net8.0-windows10.0.19041.0` → `net10.0-windows10.0.19041.0` |
| `src/Sendspin.Player/Sendspin.Player.csproj:5` | both TFMs in `<TargetFrameworks>` |
| `.github/workflows/build.yml:12` | `DOTNET_VERSION: '8.0.x'` → `'10.0.x'` |
| `.github/workflows/build.yml:220,230` | `--framework net8.0-windows10.0.19041.0` |
| `README.md:13,69,76,369` | ".NET 8.0 Runtime/SDK", `dnf install dotnet-runtime-8.0` |
| `scripts/build.sh:16,217,224` | SDK version in the header comment and the version check |
| `scripts/build.ps1:45,95,99` | same |
| `scripts/test.sh:161` | same |
| `scripts/deploy.ps1:154` | hardcoded `bin\Debug\net8.0\linux-x64` path |

Note that `packaging/` needs **no** changes: the Flatpak, AppImage and deb
manifests pin no .NET version (the Flatpak `runtime-version: '24.08'` at
`packaging/flatpak/io.sendspin.client.yml:18` is the freedesktop runtime, not
.NET).

**Re-validation risk.** Each of these must be confirmed against `net10.0` — none
is known to be a problem, but none has been tested:

- Avalonia 11.2.5 (and `Avalonia.Diagnostics`, Fluent themes, Inter fonts)
- `Sendspin.SDK 6.*` — a floating version, so its resolved build may itself shift
- `Silk.NET.OpenAL` 2.22.0 and `Silk.NET.OpenAL.Soft.Native` 1.23.1
- `Microsoft.Extensions.*` 8.0.x — the obvious candidates to bump to 10.0.x
- `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (Windows only; the least maintained
  of the set)
- `NAudio` 2.2.1, `DiscordRichPresence` 1.2.1.24, `CommunityToolkit.Mvvm` 8.4.0
- Test stack: `Microsoft.NET.Test.Sdk` 17.9.0, xunit 2.7.0, Moq 4.20.70,
  FluentAssertions 6.12.0, coverlet 6.0.1
- `TreatWarningsAsErrors` plus `AnalysisLevel: latest` means **any new analyzer
  diagnostic in the .NET 10 SDK becomes a build error.** Expect to fix warnings
  that did not exist under .NET 8; this is the most likely source of churn.

### Strategic alternative considered: drop the .NET SDK for the C++ library

Raised while this assessment was being written: if the project moved off
`Sendspin.SDK` (.NET) to the C++ Sendspin library, would that remove the need for
.NET altogether?

**No — and it would make the macOS picture harder, not easier.**

The SDK dependency really is thin. `Sendspin.SDK` appears in 13 of the 34 C#
files, across 25 reference lines, using 6 namespaces (`Audio`, `Client`,
`Connection`, `Discovery`, `Models`, `Synchronization`). But the *application* is
.NET through and through: 5,398 lines of C#, 3 Avalonia `.axaml` views, the
`Microsoft.Extensions.*` DI/logging/configuration stack, `CommunityToolkit.Mvvm`,
the entire `IPlatformInitializer`/`IPlatformPaths`/`INotificationService`
abstraction layer, Silk.NET OpenAL bindings, NAudio on Windows, and Discord
presence. Replacing the SDK leaves all of that in place and *adds* a P/Invoke
interop layer plus per-RID native binaries to build, sign and ship — for macOS
that means `osx-arm64` and `osx-x64` builds of the C++ library and signing for
the dylib, on top of everything in this document.

**Crucially, it fixes none of the findings above.** Every one of them is owned by
this repo or its toolchain, not by the SDK:

| Finding | Owned by |
|---|---|
| NU1903 restore blocker | an unused package reference in this repo |
| `net8.0` vs installed runtime | toolchain |
| `#if WINDOWS` → Linux fall-through | `App.axaml.cs` |
| XDG paths, `notify-send`, `libspeexdsp.so.1` | `Sendspin.Platform.Linux` |
| bare `catch {}` on resampler construction | this repo |
| no macOS CI, publish or `.app` | this repo's plumbing |

Removing .NET *entirely* would mean rewriting the desktop application, UI
included, in C++ against another toolkit. That is a new product rather than a
dependency swap, and it would give up the one thing that currently works on macOS
for free: Avalonia selected its macOS backend with no code in this repo asking it
to, which is why the app launches at all ([section 5](#it-really-does-run)).

**Unassessed.** The C++ library is not checked out on this machine and was not
evaluated. Nothing is claimed here about its scope, API, maturity or platform
support — only about what swapping it in would and would not change on the .NET
side. One narrower possibility is worth a real look rather than a guess: *if* the
library provides a native CoreAudio path, it could displace the OpenAL and
SpeexDSP layer, which bears directly on [(c)](#c-real-macos-platform-support)
item 4 and on [(f)](#f-stop-silently-swallowing-resampler-construction-failures).

**Effect on the follow-ups if this direction were taken:**
[(b)](#b-retarget-net80--net100) and [(c)](#c-real-macos-platform-support) would
need revisiting — (b) especially, being half a day of dependency re-validation on
a stack that might be replaced.
[(a)](#a-drop-the-unused-vulnerable-tmdsdbusprotocol-reference),
[(d)](#d-remove-the-duplicate-unreferenced-implementations),
[(e)](#e-give-sendspinplayertests-some-tests) and
[(f)](#f-stop-silently-swallowing-resampler-construction-failures) are unaffected;
they are about this repo's own code and are worth doing either way.

### Follow-up tasks

#### (a) Drop the unused vulnerable `Tmds.DBus.Protocol` reference

Delete `src/Sendspin.Player.Services/Sendspin.Player.Services.csproj:22`. The
package has zero source references and no D-Bus code exists in the repo. Verify
with `dotnet build Sendspin.Player.sln` (no `-p:NuGetAudit=false`) succeeding on
Linux, Windows and macOS.

**The reference is not random cruft, and the task is not complete without the
docs.** It is the residue of a design that was planned and then built a different
way: notifications were specified as D-Bus and implemented as `notify-send`. The
documentation still describes the plan, so anyone reading it would reasonably
re-add the package:

- `README.md:54` — describes `Sendspin.Platform.Linux/` as "OpenAL audio,
  **D-Bus notifications**".
- `docs/IMPLEMENTATION_PLAN.md:22,36` — list D-Bus as the Linux notification
  mechanism.
- `docs/IMPLEMENTATION_PLAN.md:106-107,152` — specify a
  `Notifications/DBusNotificationService.cs` using `Tmds.DBus` (note: `Tmds.DBus`
  0.21.1, a *different* package from the `Tmds.DBus.Protocol` 0.20.0 actually
  referenced — the plan was never followed even in its choice of package).

Either correct these to say `notify-send`, or — if D-Bus notifications are still
wanted — record that as its own task rather than leaving an unused vulnerable
package standing in for it.

*Size: minutes for the csproj line, plus a short docs pass. No dependencies. Do
this first — it unblocks CI on every platform and clears a high-severity
advisory.*

#### (b) Retarget `net8.0` → `net10.0`

Apply the table above, then re-run build and tests on all three platforms and
confirm the CI matrix is green. Budget time for analyzer warnings promoted to
errors.

*Size: half a day, mostly re-validation. Best done after (a), so the build is
green before the TFM moves.*

#### (c) Real macOS platform support

The substantive piece, and the only one that is genuinely about macOS. Roughly in
dependency order:

1. **Runtime platform selection.** Replace the two-branch compile-time `#if
   WINDOWS` in `src/Sendspin.Player/App.axaml.cs:12-16` and `:101-105` with a
   real three-way choice, and fix the inaccurate "detected at runtime" docstring
   at `:84-88` to match whatever the new mechanism actually is.
2. **`Sendspin.Platform.MacOS`** implementing `IPlatformInitializer`, alongside
   the existing Linux and Windows platform projects.
3. **`MacOSPaths`** — `~/Library/Application Support/sendspin`,
   `~/Library/Caches/sendspin`, `~/Library/Logs/sendspin`, replacing the XDG
   locations `LinuxPaths` currently creates on Macs.
4. **Audio.** `OpenALAudioPlayer` may be reusable as-is given the device opens
   cleanly; decide whether to share it from `Sendspin.Platform.Shared` or write a
   CoreAudio backend. Resolve the `libspeexdsp.so.1` name — either ship/locate
   `libspeexdsp.dylib` or accept the linear-interpolation fallback and **log the
   downgrade** rather than taking it silently.
5. **Notifications.** A `UNUserNotificationCenter` implementation, replacing the
   `notify-send` probe that always fails on macOS.
6. **Publish and CI.** `osx-arm64` and `osx-x64` targets in `Makefile:31`/`:187`,
   and a `macos-latest` job in `.github/workflows/build.yml` mirroring
   `build-linux`.
7. **`.app` bundling (unsigned)** — `Info.plist`, `.icns` icon, bundle layout so
   the build is a double-clickable `Sendspin.app`. An ordinary engineering task,
   and enough for anyone building locally.
8. **Code signing and notarization** — *split this out; it is not an engineering
   task.* Gatekeeper only accepts a **downloaded** build if it is signed with a
   Developer ID certificate and notarized, which requires a paid Apple Developer
   Program membership, an organizational certificate, and CI secrets to hold it.
   That is a funding and governance decision the project has to make before a
   contributor can start, and it commits the project to maintaining a
   distribution channel. Do not fold it into the engineering work.
9. **README** — update the "Windows and Linux" framing (`README.md:3`) and the
   requirements list (`README.md:11-16`).

Also settle the open question from [section 4](#the-one-target-that-does-not-build):
whether the Windows inner build being skipped on non-Windows hosts is guaranteed
SDK behaviour or incidental. If incidental, condition the Windows TFM on the host
OS explicitly so it cannot start failing.

*Size: multi-task epic. Split at least into (platform project + runtime
selection + paths), (audio + notifications), and (publish + CI + bundling +
signing). Depends on (b).*

#### (d) Remove the duplicate, unreferenced implementations

Two files, neither referenced anywhere in the repo:

**`src/Sendspin.Player/Configuration/AppPaths.cs`** — a second XDG path
implementation duplicating `LinuxPaths`.

**`src/Sendspin.Player.Services/Audio/SpeexResampler.cs`** — not merely "a second
SpeexDSP binding". It is a complete parallel type set, using the *same type
names* in a different namespace:

| In `Sendspin.Player.Services.Audio` | Duplicates |
|---|---|
| `IDynamicResampler` (`:10`) | `src/Sendspin.Core/Audio/IDynamicResampler.cs:7` |
| `ResamplerQuality` (`:36`) | `src/Sendspin.Core/Audio/IDynamicResampler.cs:33` |
| `DynamicResamplerFactory` (`:54`) | `src/Sendspin.Platform.Linux/Audio/DynamicResamplerFactory.cs:10` |
| `LinearInterpolationResampler` (`:218`) | `src/Sendspin.Platform.Shared/Audio/LinearInterpolationResampler.cs:9` |

Identical names in a parallel namespace is the worst version of this problem: a
`using` change silently swaps which implementation you get, and a fix applied to
one is invisible in the other. That is not hypothetical here — the bare-`catch`
defect in follow-up [(f)](#f-stop-silently-swallowing-resampler-construction-failures)
exists in *both* copies, in slightly different form, and would have to be fixed
twice.

Confirm with a solution-wide search, delete both files, rebuild.

Worth doing **early — right after (a), and before (b) and (c)**: fewer files to
re-validate under a new SDK, and leaving two path implementations in the tree
while adding a third for macOS is how the wrong one gets extended.

*Size: under an hour. Independent of everything else.*

#### (e) Give `Sendspin.Player.Tests` some tests

The project contains no test source files at all — see
[section 7](#7-tests-cannot-run-and-there-are-none). It has a full test stack
referenced (xunit, Moq, FluentAssertions, coverlet) and nothing to run with it.

This matters more than an empty project usually would, because **CI already runs
`dotnet test`** — at `.github/workflows/build.yml:43` (Linux) and `:213`
(Windows), and `make test` (`Makefile:145-147`) does the same locally. Those
steps pass today by running zero tests. The green check on every PR is currently
evidence of nothing.

Highest-value first targets, all pure logic and all directly relevant to the
platform work: `DynamicResamplerFactory` fallback selection,
`LinearInterpolationResampler` correctness, and the `IPlatformPaths`
implementations.

*Size: open-ended; start with a handful of meaningful tests rather than a
coverage target. Needs a runtime matching the test TFM — on this machine that
means either installing the .NET 8 runtime or completing (b); it is **not**
blocked on (b) in general, and on any machine with a .NET 8 runtime it is
unblocked today.*

#### (f) Stop silently swallowing resampler construction failures

`src/Sendspin.Platform.Linux/Audio/DynamicResamplerFactory.cs:20-23` catches every
exception from constructing `LinuxSpeexResampler` and falls back with no log
output; its duplicate at `src/Sendspin.Player.Services/Audio/SpeexResampler.cs:64`
uses a fully empty `catch { }`. Keep the `IsAvailable` guard — that branch is a
legitimate, expected condition — but narrow the `catch` and log the fallback at
warning level, including why.

Platform-independent: on Linux this equally masks a broken or ABI-mismatched
`libspeexdsp`. It matters on macOS because the fallback happens on every run, so
users silently get lower-quality resampling with nothing in the logs to say so.

If (d) lands first, only one copy needs fixing.

*Size: under an hour. Do after (d) to avoid fixing it twice.*
