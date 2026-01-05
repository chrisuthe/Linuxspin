# Sendspin Linux Client

A Linux desktop application for synchronized multi-room audio playback using the Sendspin protocol. Built with Avalonia UI and PipeWire audio.

## Features

- **Synchronized multi-room audio**: Play audio in perfect sync with other Sendspin clients
- **Native Linux experience**: PipeWire audio, D-Bus notifications, XDG directory support
- **Low-latency audio**: Sub-millisecond sync accuracy via Kalman filter clock synchronization

## Requirements

- .NET 8.0 Runtime (or use self-contained build)
- PipeWire or PulseAudio audio server
- Linux x64 or ARM64

## Installation

### AppImage (Recommended)
```bash
chmod +x Sendspin-x86_64.AppImage
./Sendspin-x86_64.AppImage
```

### Flatpak
```bash
flatpak install sendspin.flatpak
flatpak run io.sendspin.client
```

### Debian/Ubuntu (.deb)
```bash
sudo dpkg -i sendspin_1.0.0_amd64.deb
sendspin
```

### From Source
```bash
dotnet restore
dotnet build
dotnet run --project src/SendspinClient.Linux
```

## Architecture

This client uses the [Sendspin.SDK](https://www.nuget.org/packages/Sendspin.SDK) NuGet package for all protocol handling, clock synchronization, and audio pipeline orchestration. The SDK is shared with the Windows client.

```
src/
├── SendspinClient.Linux/           # Avalonia UI application
├── SendspinClient.Linux.Services/  # Linux platform services
│   ├── Audio/                      # PipeWire audio player
│   ├── Notifications/              # D-Bus notifications
│   └── Discord/                    # Rich presence integration
└── SendspinClient.Linux.Tests/     # Unit tests
```

---

## Development Workflow

This project supports cross-platform development from Windows targeting Linux.

### Prerequisites

**On Windows (Development Machine):**
- .NET 8.0 SDK
- Visual Studio 2022 or VS Code with C# extension
- Git for Windows (includes rsync for deployment)
- SSH client (built into Windows 10+)

**On Fedora (Test Machine):**
- SSH server enabled: `sudo systemctl enable --now sshd`
- .NET 8.0 Runtime (for framework-dependent builds): `sudo dnf install dotnet-runtime-8.0`
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
cd ~/sendspin-linux
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
    "processName": "sendspin",
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
ssh developer@fedora.local 'cd ~/sendspin-linux && ./scripts/test.sh'

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
- Push to `master`, `main`, or `develop` branches
- Pull requests to `master` or `main`
- Git tags matching `v*` (releases)
- Manual workflow dispatch

### Jobs

| Job | Description |
|-----|-------------|
| `build` | Compile for linux-x64 and linux-arm64 |
| `test` | Run unit tests with coverage |
| `security` | CodeQL analysis and dependency review |
| `package-appimage` | Create AppImage artifacts |
| `package-deb` | Create .deb packages |
| `package-flatpak` | Create Flatpak bundle |
| `release` | Create GitHub release with all artifacts |

### Creating a Release

```bash
# Tag and push to trigger release
git tag v1.2.3
git push origin v1.2.3
```

The workflow will automatically:
1. Build for all architectures
2. Run tests and security scans
3. Create AppImage, .deb, and Flatpak packages
4. Upload to GitHub Releases with checksums

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
# Install .NET 8.0 SDK from https://dot.net
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

- [Sendspin Windows Client](https://github.com/your-org/windowsSpin) - Windows WPF client
- [Sendspin CLI](https://github.com/your-org/sendspin-cli) - Python CLI reference implementation
- [Music Assistant](https://music-assistant.io/) - The server this client connects to

## License

MIT License - see LICENSE file for details.
