# Sendspin Player

A desktop Sendspin **player**: it turns the machine it runs on into a synchronized music endpoint
that feels like it belongs there. Windows 11, macOS (Apple Silicon) and Linux, built on
[Avalonia](https://avaloniaui.net/) 12 and the
[Sendspin.SDK](https://www.nuget.org/packages/Sendspin.SDK).

**Read [`docs/COMPLIANCE.md`](docs/COMPLIANCE.md) before deploying this.** It states which spec
roles are implemented, which are not, and — importantly — **which servers a given build can
actually talk to**. Builds on SDK 9.3.2 have no transport encryption and cannot connect to a
server that requires it.

| Document | What it is for |
|---|---|
| [`docs/COMPLIANCE.md`](docs/COMPLIANCE.md) | What is implemented, what is not, and which servers a build can talk to |
| [`docs/NEXT_STEPS.md`](docs/NEXT_STEPS.md) | What remains, why it stopped, and the first concrete action for each |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Why the code is shaped this way, and the **measured** platform and SDK facts it rests on |

If you are picking this up cold, read `docs/ARCHITECTURE.md` first. Several things in it contradict
the obvious answer, and each one cost real effort to establish.

## What it does

- **Synchronized playback** against the rest of a Sendspin group, using the SDK's Kalman clock
  filter and a real hardware audio clock rather than the OS wall clock.
- **Either connection mode**: it advertises itself for a server to connect to, or discovers servers
  itself. One or the other, never both at once — connection.md allows a client exactly one
  connection method at a time.
- **Native OS media integration** — the Windows 11 media flyout, MPRIS on Plasma 6 and GNOME, and
  Now Playing / Control Center on macOS — so hardware media keys work without the app being
  focused.
- **Tray / status item**, start-minimized, single-instance, and notifications with per-event
  toggles.
- **Real measured output latency** per platform, not a constant, plus a per-device manual offset
  for the Bluetooth and AirPlay tail that no API reports.
- **A diagnostics view** showing sync error, correction band, playback rate in ppm, buffer depth,
  clock offset and drift, and — the most useful single field — whether the timing source is
  actually the audio hardware clock.
- **Native shell, system theme and accent, platform font.** Resizable, with the OS's own
  decorations; follows the desktop's light/dark setting and accent live. The layout inside the
  window comes from Sendspin for Windows; the colours do not.

## Requirements

- **.NET 10** runtime, or use a self-contained build.
- **Linux**: PipeWire or PulseAudio, and OpenAL Soft. Under GNOME the tray needs the AppIndicator
  extension (see [Known limitations](#known-limitations)).
- **Windows**: Windows 10 2004 (build 19041) or later.
- **macOS**: macOS 13 or later, Apple Silicon. Intel Macs are not built — .NET cannot emit a
  universal binary and the bundled `libopenal.dylib` is arm64-only.

## Installation

### Linux — AppImage

```bash
chmod +x Sendspin-Player-x86_64.AppImage
./Sendspin-Player-x86_64.AppImage
```

### Linux — Flatpak

```bash
flatpak install Sendspin-Player.flatpak
flatpak run io.sendspin.client
```

### Windows

Framework-dependent: install the [.NET 10 runtime](https://dot.net) and run `Sendspin.Player.exe`.

### macOS

Open the dmg and drag the app to Applications. **Launch it from Finder, not from a terminal** —
macOS 15 and later gate all local-network access behind a grant that only a Finder launch prompts
for, and a terminal launch is auto-allowed in a way that hides whether the grant works.

Current builds are **unsigned**; see `docs/COMPLIANCE.md` for what that costs and why ad-hoc
signing is not used as a substitute.

## Architecture

Protocol handling, the clock filter, the volume curve and the audio pipeline all live in
`Sendspin.SDK`. This repository is the desktop player around it: the platform integrations the SDK
cannot provide, and the UI.

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

Two things about this layout are load-bearing rather than tidy:

**Platform choice is a runtime decision.** Everything branches on `OperatingSystem.IsX()`. The one
exception is naming a concrete `IPlatformInitializer`, which needs a reference the other target
frameworks cannot have; that lives in a single per-TFM file under
`src/Sendspin.Player/PlatformSelection/`, and those files contain wiring only.

**Decisions live in `Core`/`Platform.Shared`, adapters stay thin.** The volume curve, the
media-session state mapping, the sync-correction limits and the command router are all
platform-neutral and unit-tested. That is not only for testability: the duplicated-per-adapter
version of this had already drifted into a real bug, with Windows applying the loudness curve a
second time on top of the SDK's.

## Building

```bash
dotnet test  src/Sendspin.Tests/Sendspin.Tests.csproj

# Linux
dotnet publish src/Sendspin.Player/Sendspin.Player.csproj -c Release \
  -f net10.0 -r linux-x64 --self-contained -o publish/linux-x64

# Windows (cross-compiles from any host)
dotnet publish src/Sendspin.Player/Sendspin.Player.csproj -c Release \
  -f net10.0-windows10.0.19041.0 -r win-x64 --no-self-contained -o publish/win-x64

# macOS — only builds ON macOS, and needs the workload: dotnet workload install macos
dotnet publish src/Sendspin.Player/Sendspin.Player.csproj -c Release \
  -f net10.0-macos -r osx-arm64 -o publish/osx-arm64
```

The macOS head is added to `TargetFrameworks` only when building on macOS, because the `macos`
workload does not exist on other hosts and its presence would break restore there. If the runner's
Xcode version disagrees with the pinned SDK pack, override it:
`-p:SendspinMacOSPlatformVersion=<version>`.

`TreatWarningsAsErrors` and `AnalysisLevel=latest` are on. Turning either off is not an acceptable
way to fix a build.

### Windowing backend (Linux)

Under a Wayland session the app runs on Avalonia 12.1's native Wayland backend, which gives correct
fractional HiDPI. Where there is no session — an X11 desktop, VNC, a forwarded `DISPLAY`, a CI
container — it runs on X11. That is a decision, not a fallback that happens to work: `WAYLAND_DISPLAY`
is read up front, because `UseWayland()` with no compositor to talk to aborts rather than degrades.

The Wayland backend is marked experimental and its own README warns that compositor
crash-and-restart is expected, so X11 stays one variable away:

```bash
SENDSPIN_X11=1 ./Sendspin.Player      # force X11 (XWayland under a Wayland session)
SENDSPIN_WAYLAND=1 ./Sendspin.Player  # force Wayland past a session this cannot detect
```

`SENDSPIN_X11` wins if both are set. Nothing else changes with the backend: every desktop
integration here (MPRIS, StatusNotifierItem, notifications, portals) is D-Bus and identical either
way, and the two protocols the Wayland backend does not bind are already routed around —
screensaver inhibition goes through `org.freedesktop.portal.Inhibit`, and raising the window goes
through the activation token the notification daemon hands back.

## Configuration

One file, same name on every platform:

| Platform | Path |
|---|---|
| Linux | `$XDG_CONFIG_HOME/sendspin/settings.json` (default `~/.config/sendspin/`) |
| Windows | `%LocalAppData%\Sendspin\config\settings.json` |
| macOS | `~/Library/Application Support/Sendspin/settings.json` |

Everything the UI changes is written there immediately; there is no apply button. Two settings are
genuine physical calibration and are exposed on purpose — the **static delay**, which aligns this
player against the rest of its group, and a **per-device latency offset** for outputs whose delay
no API reports. Buffer depths and sync-correction limits are deliberately *not* exposed: they have
correct answers, and a knob only invites making the player worse.

## Known limitations

- **Transport encryption and pairing are not implemented**, because the pinned SDK does not provide
  them and hand-rolling Noise or CPace here would be worse than not having it. A build cannot
  connect to a server that requires encryption. See `docs/COMPLIANCE.md`.
- **Availability is not withheld until the clock filter converges.** The SDK proceeds after a
  timeout and owns the message that reports availability, so this cannot be fixed from here. It is
  a declared gap, not a ticked box.
- **GNOME tray needs the AppIndicator extension.** GNOME Shell has no `StatusNotifierWatcher`, no
  tray portal exists, and Avalonia's X11 fallback is a stub that logs "not implemented" — and is
  not even reached, so on vanilla GNOME the icon simply does not appear with no error. Ubuntu ships
  the extension; Fedora, Debian GNOME and vanilla do not. `org.freedesktop.portal.Background` is the
  alternative surface, and Plasma 6.7 renders it as "Background Apps".
- **Windows 11 hides notification-area icons by default** and Microsoft documents that this cannot
  be controlled programmatically, so the tray is not a dependable transport surface there. A taskbar
  overlay badge carries the always-visible affordance instead.
- **macOS builds are unsigned**, which means Now Playing, notifications and local-network discovery
  are unverified. `docs/COMPLIANCE.md` lists what a Developer ID certificate unblocks.
- **Sync accuracy has not been measured against a second client** on real hardware. The
  instrumentation to measure it ships in the diagnostics view; the numbers do not.

Each of these is tracked with a first action in [`docs/NEXT_STEPS.md`](docs/NEXT_STEPS.md).

## Licence

MIT — see [`LICENSE`](LICENSE).

---

## Development Workflow

This project supports cross-platform development from Windows targeting Linux.

### Prerequisites

**On Windows (Development Machine):**
- .NET 10 SDK
- Visual Studio 2022 or VS Code with C# extension
- Git for Windows (includes rsync for deployment)
- SSH client (built into Windows 10+)

**On Fedora (Test Machine):**
- SSH server enabled: `sudo systemctl enable --now sshd`
- .NET 10 runtime (for framework-dependent builds): `sudo dnf install dotnet-runtime-10.0`
- PipeWire (default on Fedora)

### Quick Start

```powershell
# Windows: Build for Linux
.\scripts\build.ps1 -Configuration Release -Publish

# Windows: Deploy to Fedora machine
.\scripts\deploy.ps1 -TargetHost fedora.local -Run

# Windows: Watch and auto-deploy
.\scripts\deploy.ps1 -TargetHost fedora.local -Watch
```

### Development Options

#### Option 1: Cross-Compile on Windows (Recommended)

.NET's cross-compilation works seamlessly for Linux targets from Windows.

```powershell
# Quick debug build
.\scripts\build.ps1

# Release build with single-file output
.\scripts\build.ps1 -Publish -SingleFile

# Build for ARM64 (Raspberry Pi, etc.)
.\scripts\build.ps1 -Runtime linux-arm64 -Publish
```

#### Option 2: Remote Build on Linux

For scenarios requiring native Linux compilation:

```bash
# SSH to Fedora and build there
ssh user@fedora.local
cd ~/sendspin-player
./scripts/build.sh --release
```

#### Option 3: GitHub Actions

Push to trigger automated builds on Linux runners:

```bash
git push origin feature/my-change
# Check Actions tab for build status
```

### Deployment to Test Machine

#### Initial Setup

1. Create deployment configuration:
```powershell
Copy-Item .deploy.json.template .deploy.json
# Edit .deploy.json with your Fedora machine details
```

Example `.deploy.json`:
```json
{
    "host": "fedora.local",
    "user": "developer",
    "path": "~/sendspin",
    "port": 22
}
```

2. Set up SSH key authentication:
```powershell
ssh-copy-id developer@fedora.local
```

#### Deployment Commands

```powershell
# Basic deployment
.\scripts\deploy.ps1

# Deploy and run
.\scripts\deploy.ps1 -Run

# Deploy, kill existing, run, and attach to output
.\scripts\deploy.ps1 -Kill -Run -Attach

# Watch mode - auto-deploy on file changes
.\scripts\deploy.ps1 -Watch

# Dry run - see what would be deployed
.\scripts\deploy.ps1 -DryRun
```

### Remote Debugging

#### VS Code Remote Debugging

1. Install vsdbg on the Fedora machine:
```bash
curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/.vsdbg
```

2. Add to `.vscode/launch.json`:
```json
{
    "name": "Attach to Sendspin (Remote)",
    "type": "coreclr",
    "request": "attach",
    "processName": "Sendspin.Player",
    "pipeTransport": {
        "pipeCwd": "${workspaceFolder}",
        "pipeProgram": "ssh",
        "pipeArgs": ["-p", "22", "developer@fedora.local"],
        "debuggerPath": "~/.vsdbg/vsdbg"
    },
    "sourceFileMap": {
        "/home/developer/sendspin": "${workspaceFolder}"
    }
}
```

3. Deploy and attach:
```powershell
.\scripts\deploy.ps1 -Debug -Run
# Then attach debugger in VS Code
```

### Testing

```powershell
# Run all tests locally (Windows)
dotnet test

# Run tests on Linux (via SSH)
ssh developer@fedora.local 'cd ~/sendspin-player && ./scripts/test.sh'

# Run with coverage
./scripts/test.sh --coverage
```

### Building Packages

#### Using Makefile (Linux)

```bash
make appimage          # Build AppImage
make deb               # Build .deb package
make flatpak           # Build Flatpak
make packages          # Build all formats
```

#### Using PowerShell (Windows)

```powershell
# Build then use deploy script to transfer
.\scripts\build.ps1 -Publish

# Package creation is automated in GitHub Actions
```

---

## CI/CD Pipeline

The GitHub Actions workflow (`.github/workflows/build.yml`) provides:

### Triggers
- Push to `master` or `main` branches
- Pull requests to `master` or `main`
- Manual workflow dispatch

### Jobs

| Job | Description |
|-----|-------------|
| `build-linux` | Compile for linux-x64 and linux-arm64, run tests |
| `build-windows` | Compile for win-x64, run tests |
| `package-flatpak` | Create Flatpak bundle (on main/master) |
| `package-appimage` | Create AppImage artifact (on main/master) |
| `package-windows` | Create Windows portable package (on main/master) |

---

## Build Scripts Reference

### scripts/build.ps1 (Windows)

```powershell
.\scripts\build.ps1 [OPTIONS]

Options:
  -Configuration <Debug|Release>  Build configuration (default: Debug)
  -Runtime <linux-x64|linux-arm64>  Target runtime (default: linux-x64)
  -Publish                        Create publishable output
  -SingleFile                     Create single-file executable
  -SelfContained                  Include .NET runtime
  -Clean                          Clean before building
  -OutputPath <path>              Custom output directory
```

### scripts/build.sh (Linux)

```bash
./scripts/build.sh [OPTIONS]

Options:
  -c, --configuration <cfg>  Build configuration (default: Debug)
  -r, --runtime <rid>        Target runtime (default: linux-x64)
  -p, --publish              Create publishable output
  --single-file              Create single-file executable
  --clean                    Clean before building
  -t, --test                 Run tests after build
  --appimage                 Build AppImage package
  --deb                      Build .deb package
  --flatpak                  Build Flatpak package
  --all                      Build all package formats
```

### scripts/deploy.ps1 (Windows)

```powershell
.\scripts\deploy.ps1 [OPTIONS]

Options:
  -TargetHost <hostname>    SSH hostname (or set SENDSPIN_DEPLOY_HOST)
  -TargetUser <username>    SSH username (default: current user)
  -TargetPath <path>        Remote path (default: ~/sendspin)
  -SourcePath <path>        Local artifacts path
  -Run                      Start app after deployment
  -Attach                   Attach to app output (implies -Run)
  -Kill                     Kill existing process first
  -Debug                    Setup remote debugging
  -Watch                    Watch for changes and auto-deploy
  -DryRun                   Show what would be done
```

### scripts/deploy.sh (Linux)

```bash
./scripts/deploy.sh [HOSTNAME] [OPTIONS]

Options:
  -u, --user <user>    SSH username
  -p, --path <path>    Remote path
  -r, --run            Run after deployment
  -a, --attach         Attach to output
  --kill               Kill existing process
  --debug              Setup remote debugging
  -w, --watch          Watch mode
```

### Makefile Targets

```bash
make                  # Build debug
make release          # Build release
make test             # Run tests
make coverage         # Run tests with coverage
make publish          # Create publishable artifacts
make appimage         # Build AppImage
make deb              # Build .deb
make flatpak          # Build Flatpak
make packages         # Build all packages
make deploy           # Deploy to test machine
make deploy-run       # Deploy and run
make clean            # Clean build artifacts
make help             # Show all targets
```

---

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `SENDSPIN_DEPLOY_HOST` | Default deployment hostname | - |
| `SENDSPIN_DEPLOY_USER` | Default SSH username | Current user |
| `SENDSPIN_DEPLOY_PATH` | Default remote path | `~/sendspin` |
| `SENDSPIN_DEPLOY_PORT` | Default SSH port | `22` |
| `SENDSPIN_DEPLOY_KEY` | SSH private key path | System default |

---

## Troubleshooting

### Build Issues

**"dotnet not found"**
```powershell
# Install .NET 10 SDK from https://dot.net
winget install Microsoft.DotNet.SDK.8
```

**"rsync not found" on Windows**
```powershell
# rsync is included with Git for Windows
# Make sure Git Bash is in PATH, or use WSL
wsl rsync --version
```

### Deployment Issues

**"Cannot connect to host"**
```bash
# Check SSH connectivity
ssh -v user@fedora.local

# Ensure SSH server is running on Fedora
sudo systemctl status sshd
sudo systemctl enable --now sshd

# Check firewall
sudo firewall-cmd --add-service=ssh --permanent
sudo firewall-cmd --reload
```

**"Permission denied" on remote**
```bash
# Set up SSH key authentication
ssh-copy-id user@fedora.local
```

### Runtime Issues

**"libicu not found"**
```bash
# Install ICU libraries on Fedora
sudo dnf install libicu
```

**"PipeWire connection failed"**
```bash
# Check PipeWire status
systemctl --user status pipewire

# Restart if needed
systemctl --user restart pipewire
```

---

## Related Projects

- [Sendspin CLI](https://github.com/chrisuthe/sendspin-cli) - Python CLI reference implementation
- [Music Assistant](https://music-assistant.io/) - The server this client connects to
