<#
.SYNOPSIS
    Deploy Sendspin Linux Client to a remote Fedora test machine.

.DESCRIPTION
    This script deploys the built application to a remote Linux machine for testing.
    It uses rsync for efficient incremental file transfers and supports
    remote execution for quick testing iterations.

.PARAMETER TargetHost
    SSH hostname or IP address of the target machine.
    Can also be set via SENDSPIN_DEPLOY_HOST environment variable.

.PARAMETER TargetUser
    SSH username for the target machine. Default is current username.
    Can also be set via SENDSPIN_DEPLOY_USER environment variable.

.PARAMETER TargetPath
    Remote directory path for deployment. Default is ~/sendspin.

.PARAMETER SourcePath
    Local path to the built artifacts. Default is artifacts/linux-x64.

.PARAMETER SshKey
    Path to SSH private key file. Default uses system default.
    Can also be set via SENDSPIN_DEPLOY_KEY environment variable.

.PARAMETER SshPort
    SSH port number. Default is 22.

.PARAMETER Run
    Start the application after deployment.

.PARAMETER Attach
    Attach to the running application's output (implies -Run).

.PARAMETER Kill
    Kill any running instance before deployment.

.PARAMETER Debug
    Deploy debug build and attach debugger (requires vsdbg on remote).

.PARAMETER Watch
    Watch for file changes and auto-deploy (requires fswatch or similar).

.PARAMETER DryRun
    Show what would be deployed without actually transferring files.

.EXAMPLE
    .\deploy.ps1 -TargetHost fedora-test.local
    Deploy to fedora-test.local with default settings

.EXAMPLE
    .\deploy.ps1 -TargetHost 192.168.1.50 -TargetUser dev -Run
    Deploy and run the application

.EXAMPLE
    .\deploy.ps1 -TargetHost fedora -Kill -Run -Attach
    Kill existing, deploy, run, and watch output

.NOTES
    Author: Sendspin Team
    Requires: OpenSSH client, rsync (via WSL or Git Bash)
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$TargetHost = $env:SENDSPIN_DEPLOY_HOST,

    [string]$TargetUser = $env:SENDSPIN_DEPLOY_USER,

    [string]$TargetPath = '~/sendspin',

    [string]$SourcePath,

    [string]$SshKey = $env:SENDSPIN_DEPLOY_KEY,

    [int]$SshPort = 22,

    [switch]$Run,

    [switch]$Attach,

    [switch]$Kill,

    [switch]$Debug,

    [switch]$Watch,

    [switch]$DryRun
)

# ==============================================================================
# Configuration
# ==============================================================================

$ErrorActionPreference = 'Stop'
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot

# Color output helpers
function Write-Info { param($Message) Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Warn { param($Message) Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Err { param($Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

# ==============================================================================
# Configuration File Support
# ==============================================================================

$configFile = Join-Path $RepoRoot '.deploy.json'
if (Test-Path $configFile) {
    Write-Info "Loading deployment configuration from .deploy.json"
    $config = Get-Content $configFile | ConvertFrom-Json

    if (-not $TargetHost -and $config.host) { $TargetHost = $config.host }
    if (-not $TargetUser -and $config.user) { $TargetUser = $config.user }
    if ($config.path) { $TargetPath = $config.path }
    if ($config.port) { $SshPort = $config.port }
    if (-not $SshKey -and $config.key) { $SshKey = $config.key }
}

# ==============================================================================
# Validation
# ==============================================================================

if (-not $TargetHost) {
    Write-Err "Target host is required. Specify -TargetHost or set SENDSPIN_DEPLOY_HOST"
    Write-Host ""
    Write-Host "You can also create a .deploy.json file in the repository root:" -ForegroundColor Yellow
    Write-Host @"
{
    "host": "your-fedora-machine.local",
    "user": "yourusername",
    "path": "~/sendspin",
    "port": 22
}
"@ -ForegroundColor Gray
    exit 1
}

if (-not $TargetUser) {
    $TargetUser = $env:USERNAME
    Write-Info "Using current username: $TargetUser"
}

# Determine source path
if (-not $SourcePath) {
    $SourcePath = Join-Path $RepoRoot 'artifacts\linux-x64'

    # Fall back to debug build if release doesn't exist
    if (-not (Test-Path $SourcePath)) {
        $debugPath = Join-Path $RepoRoot 'src\SendspinClient.Linux\bin\Debug\net8.0\linux-x64'
        if (Test-Path $debugPath) {
            $SourcePath = $debugPath
            Write-Warn "Using debug build from: $SourcePath"
        }
    }
}

if (-not (Test-Path $SourcePath)) {
    Write-Err "Source path not found: $SourcePath"
    Write-Host "Run build first: .\scripts\build.ps1 -Publish" -ForegroundColor Yellow
    exit 1
}

# Verify we have rsync available
$rsyncCmd = $null
$sshOptions = @()

# Build SSH options
if ($SshKey) {
    $sshOptions += "-i"
    $sshOptions += $SshKey
}
$sshOptions += "-p"
$sshOptions += $SshPort
$sshOptions += "-o"
$sshOptions += "StrictHostKeyChecking=accept-new"
$sshOptions += "-o"
$sshOptions += "ConnectTimeout=10"

# Check for rsync (try WSL, Git Bash, or native)
$rsyncPaths = @(
    'rsync',                                           # Native or PATH
    'C:\Program Files\Git\usr\bin\rsync.exe',         # Git for Windows
    'C:\msys64\usr\bin\rsync.exe'                     # MSYS2
)

foreach ($path in $rsyncPaths) {
    if (Get-Command $path -ErrorAction SilentlyContinue) {
        $rsyncCmd = $path
        break
    }
}

# Try WSL rsync
if (-not $rsyncCmd) {
    try {
        $wslCheck = wsl which rsync 2>$null
        if ($wslCheck) {
            $rsyncCmd = 'wsl rsync'
            Write-Info "Using rsync via WSL"
        }
    }
    catch { }
}

if (-not $rsyncCmd) {
    Write-Err "rsync not found. Install Git for Windows, MSYS2, or WSL."
    Write-Host ""
    Write-Host "Options:" -ForegroundColor Yellow
    Write-Host "  1. Install Git for Windows (includes rsync)" -ForegroundColor White
    Write-Host "  2. Install MSYS2 and rsync: pacman -S rsync" -ForegroundColor White
    Write-Host "  3. Use WSL: wsl --install" -ForegroundColor White
    exit 1
}

Write-Info "Using rsync: $rsyncCmd"

# ==============================================================================
# SSH Helper Functions
# ==============================================================================

function Invoke-SshCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [switch]$NoThrow
    )

    $sshArgs = $sshOptions + @("${TargetUser}@${TargetHost}", $Command)

    Write-Host "  > ssh $TargetUser@$TargetHost `"$Command`"" -ForegroundColor DarkGray

    if ($DryRun) {
        Write-Host "  [DRY RUN] Would execute command" -ForegroundColor Yellow
        return
    }

    $result = & ssh @sshArgs 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $NoThrow) {
        Write-Err "SSH command failed: $result"
        exit $LASTEXITCODE
    }

    return $result
}

# ==============================================================================
# Test SSH Connection
# ==============================================================================

Write-Info "Testing SSH connection to $TargetUser@$TargetHost..."

$sshTestArgs = $sshOptions + @("${TargetUser}@${TargetHost}", "echo 'Connection OK'")
$connectionTest = & ssh @sshTestArgs 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Err "Cannot connect to $TargetUser@$TargetHost"
    Write-Host "Error: $connectionTest" -ForegroundColor Red
    Write-Host ""
    Write-Host "Troubleshooting:" -ForegroundColor Yellow
    Write-Host "  1. Verify the host is reachable: ping $TargetHost" -ForegroundColor White
    Write-Host "  2. Check SSH key is configured: ssh-copy-id $TargetUser@$TargetHost" -ForegroundColor White
    Write-Host "  3. Verify firewall allows SSH: firewall-cmd --list-services" -ForegroundColor White
    exit 1
}

Write-Success "SSH connection established"

# ==============================================================================
# Kill Existing Process (if requested)
# ==============================================================================

if ($Kill -or $Run -or $Debug) {
    Write-Info "Checking for existing sendspin process..."

    $killResult = Invoke-SshCommand -Command "pkill -f 'sendspin|SendspinClient' 2>/dev/null || true" -NoThrow
    if ($killResult) {
        Write-Info "Killed existing process"
    }
}

# ==============================================================================
# Deploy Files
# ==============================================================================

Write-Info "Deploying to $TargetUser@$TargetHost:$TargetPath..."

# Create remote directory
Invoke-SshCommand -Command "mkdir -p $TargetPath"

# Convert Windows path to rsync-compatible path
$rsyncSource = $SourcePath -replace '\\', '/'
$rsyncSource = $rsyncSource -replace '^([A-Za-z]):', '/\1'

# For WSL, convert Windows path
if ($rsyncCmd -eq 'wsl rsync') {
    $rsyncSource = $SourcePath -replace '\\', '/'
    $rsyncSource = $rsyncSource -replace '^([A-Za-z]):', '/mnt/\1'.ToLower()
}

# Build rsync command
$rsyncArgs = @(
    '-avz',                    # Archive, verbose, compress
    '--progress',              # Show progress
    '--delete',                # Delete extraneous files
    '--exclude', '*.pdb',      # Exclude debug symbols for faster transfer
    '--exclude', '*.xml',      # Exclude XML docs
    '-e', "ssh $($sshOptions -join ' ')"  # SSH options
)

if ($DryRun) {
    $rsyncArgs += '--dry-run'
}

$rsyncTarget = "${TargetUser}@${TargetHost}:${TargetPath}/"

# Add trailing slash to source to copy contents, not directory
$rsyncSource = "$rsyncSource/"

Write-Host "  Syncing: $rsyncSource -> $rsyncTarget" -ForegroundColor DarkGray

# Execute rsync
if ($rsyncCmd -eq 'wsl rsync') {
    # For WSL, we need to run the entire command through WSL
    $wslCmd = "rsync $($rsyncArgs -join ' ') '$rsyncSource' '$rsyncTarget'"
    & wsl bash -c $wslCmd
}
else {
    & $rsyncCmd @rsyncArgs $rsyncSource $rsyncTarget
}

if ($LASTEXITCODE -ne 0) {
    Write-Err "File transfer failed"
    exit $LASTEXITCODE
}

Write-Success "Files deployed successfully"

# Set executable permissions
Write-Info "Setting executable permissions..."
Invoke-SshCommand -Command "chmod +x $TargetPath/sendspin 2>/dev/null || chmod +x $TargetPath/SendspinClient.Linux 2>/dev/null || true"

# ==============================================================================
# Run Application (if requested)
# ==============================================================================

if ($Run -or $Attach -or $Debug) {
    Write-Info "Starting application on remote host..."

    $runCommand = "cd $TargetPath && "

    # Set display for X11
    $runCommand += "export DISPLAY=:0 && "

    # Use the correct binary name
    $runCommand += "if [ -f ./sendspin ]; then ./sendspin; else ./SendspinClient.Linux; fi"

    if ($Debug) {
        # Remote debugging setup
        Write-Info "Starting with remote debugger support..."

        # Check if vsdbg is installed
        $vsdbgCheck = Invoke-SshCommand -Command "test -f ~/.vsdbg/vsdbg && echo 'found'" -NoThrow
        if ($vsdbgCheck -ne 'found') {
            Write-Warn "vsdbg not found on remote. Installing..."
            Invoke-SshCommand -Command "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/.vsdbg"
        }

        Write-Host ""
        Write-Host "To attach debugger in VS Code:" -ForegroundColor Yellow
        Write-Host "  1. Open launch.json and add a 'coreclr' attach configuration" -ForegroundColor White
        Write-Host "  2. Set 'pipeTransport' with ssh connection to $TargetHost" -ForegroundColor White
        Write-Host "  3. Set 'processName' to 'sendspin' or 'SendspinClient.Linux'" -ForegroundColor White
        Write-Host ""
    }

    if ($Attach) {
        Write-Host ""
        Write-Host "=== Application Output ===" -ForegroundColor Green
        Write-Host "(Press Ctrl+C to stop)" -ForegroundColor DarkGray
        Write-Host ""

        # Run interactively
        $sshArgs = $sshOptions + @("-t", "${TargetUser}@${TargetHost}", $runCommand)
        & ssh @sshArgs
    }
    else {
        # Run in background
        $bgCommand = "cd $TargetPath && nohup bash -c '$runCommand' > /tmp/sendspin.log 2>&1 &"
        Invoke-SshCommand -Command $bgCommand

        Write-Success "Application started in background"
        Write-Host "  View logs: ssh $TargetUser@$TargetHost 'tail -f /tmp/sendspin.log'" -ForegroundColor DarkGray
    }
}

# ==============================================================================
# Watch Mode (if requested)
# ==============================================================================

if ($Watch) {
    Write-Info "Entering watch mode - monitoring for file changes..."
    Write-Host "(Press Ctrl+C to stop)" -ForegroundColor DarkGray
    Write-Host ""

    # Use FileSystemWatcher for Windows
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = $SourcePath
    $watcher.IncludeSubdirectories = $true
    $watcher.EnableRaisingEvents = $true

    $action = {
        $path = $Event.SourceEventArgs.FullPath
        $changeType = $Event.SourceEventArgs.ChangeType
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $changeType`: $path" -ForegroundColor DarkGray

        # Debounce - wait for changes to settle
        Start-Sleep -Milliseconds 500

        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Redeploying..." -ForegroundColor Yellow
        & $MyInvocation.MyCommand.Path -TargetHost $TargetHost -TargetUser $TargetUser -TargetPath $TargetPath -Kill -Run
    }

    Register-ObjectEvent $watcher 'Changed' -Action $action | Out-Null
    Register-ObjectEvent $watcher 'Created' -Action $action | Out-Null

    try {
        while ($true) { Start-Sleep -Seconds 1 }
    }
    finally {
        $watcher.EnableRaisingEvents = $false
        Get-EventSubscriber | Unregister-Event
    }
}

# ==============================================================================
# Summary
# ==============================================================================

Write-Host ""
Write-Success "Deployment complete!"
Write-Host ""
Write-Host "Remote commands:" -ForegroundColor Yellow
Write-Host "  Run:    ssh $TargetUser@$TargetHost 'cd $TargetPath && ./sendspin'" -ForegroundColor White
Write-Host "  Logs:   ssh $TargetUser@$TargetHost 'tail -f /tmp/sendspin.log'" -ForegroundColor White
Write-Host "  Kill:   ssh $TargetUser@$TargetHost 'pkill -f sendspin'" -ForegroundColor White
Write-Host "  Shell:  ssh $TargetUser@$TargetHost" -ForegroundColor White

exit 0
