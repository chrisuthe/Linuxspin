<#
.SYNOPSIS
    Build script for Sendspin Linux Client - Windows development machine targeting Linux.

.DESCRIPTION
    This script builds the Sendspin Linux client from a Windows development environment.
    It supports cross-compilation to Linux using .NET's runtime identifier system.

.PARAMETER Configuration
    Build configuration: Debug or Release. Default is Debug.

.PARAMETER Runtime
    Target runtime identifier. Default is linux-x64.
    Supported: linux-x64, linux-arm64

.PARAMETER SelfContained
    Create a self-contained deployment. Default is true for Release, false for Debug.

.PARAMETER Clean
    Clean build artifacts before building.

.PARAMETER Publish
    Create publishable output (implies Release configuration).

.PARAMETER SingleFile
    Create single-file executable (requires -Publish).

.PARAMETER OutputPath
    Custom output path for published artifacts.

.EXAMPLE
    .\build.ps1
    Quick debug build for linux-x64

.EXAMPLE
    .\build.ps1 -Configuration Release -Publish
    Create release build ready for deployment

.EXAMPLE
    .\build.ps1 -Runtime linux-arm64 -Publish -SingleFile
    Create single-file ARM64 release build

.NOTES
    Author: Sendspin Team
    Requires: .NET 8.0 SDK
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('linux-x64', 'linux-arm64')]
    [string]$Runtime = 'linux-x64',

    [switch]$SelfContained,

    [switch]$Clean,

    [switch]$Publish,

    [switch]$SingleFile,

    [string]$OutputPath
)

# ==============================================================================
# Configuration
# ==============================================================================

$ErrorActionPreference = 'Stop'
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot
$SolutionFile = Join-Path $RepoRoot 'SendspinClient.Linux.sln'
$MainProject = Join-Path $RepoRoot 'src\SendspinClient.Linux\SendspinClient.Linux.csproj'

# Color output helpers
function Write-Info { param($Message) Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Warn { param($Message) Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Err { param($Message) Write-Host "[ERROR] $Message" -ForegroundColor Red }

# ==============================================================================
# Validation
# ==============================================================================

Write-Info "Validating build environment..."

# Check .NET SDK
try {
    $dotnetVersion = dotnet --version
    Write-Info "Found .NET SDK version: $dotnetVersion"

    if (-not $dotnetVersion.StartsWith('8.')) {
        Write-Warn ".NET 8.x SDK is recommended. Current version: $dotnetVersion"
    }
}
catch {
    Write-Err ".NET SDK not found. Please install .NET 8.0 SDK."
    exit 1
}

# Verify solution exists
if (-not (Test-Path $SolutionFile)) {
    Write-Err "Solution file not found: $SolutionFile"
    exit 1
}

Write-Success "Build environment validated"

# ==============================================================================
# Build Configuration
# ==============================================================================

# If publishing, default to Release
if ($Publish -and $Configuration -eq 'Debug') {
    Write-Info "Publish requested - switching to Release configuration"
    $Configuration = 'Release'
}

# Self-contained defaults
if (-not $PSBoundParameters.ContainsKey('SelfContained')) {
    $SelfContained = ($Configuration -eq 'Release')
}

# Output path
if (-not $OutputPath) {
    $OutputPath = Join-Path $RepoRoot "artifacts\$Runtime"
}

Write-Info "Build Configuration:"
Write-Info "  Configuration: $Configuration"
Write-Info "  Runtime: $Runtime"
Write-Info "  Self-Contained: $SelfContained"
Write-Info "  Publish: $Publish"
Write-Info "  Single File: $SingleFile"
Write-Info "  Output: $OutputPath"

# ==============================================================================
# Clean (if requested)
# ==============================================================================

if ($Clean) {
    Write-Info "Cleaning build artifacts..."

    $cleanPaths = @(
        (Join-Path $RepoRoot 'artifacts'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux\bin'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux\obj'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux.Services\bin'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux.Services\obj'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux.Tests\bin'),
        (Join-Path $RepoRoot 'src\SendspinClient.Linux.Tests\obj')
    )

    foreach ($path in $cleanPaths) {
        if (Test-Path $path) {
            Remove-Item -Path $path -Recurse -Force
            Write-Info "  Removed: $path"
        }
    }

    # Run dotnet clean
    & dotnet clean $SolutionFile --configuration $Configuration --verbosity minimal
    Write-Success "Clean complete"
}

# ==============================================================================
# Restore
# ==============================================================================

Write-Info "Restoring NuGet packages..."

$restoreArgs = @(
    'restore',
    $SolutionFile,
    '--runtime', $Runtime,
    '--verbosity', 'minimal'
)

& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) {
    Write-Err "Package restore failed"
    exit $LASTEXITCODE
}

Write-Success "Packages restored"

# ==============================================================================
# Build
# ==============================================================================

Write-Info "Building solution..."

$buildArgs = @(
    'build',
    $SolutionFile,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--no-restore',
    '--verbosity', 'minimal'
)

# Add self-contained flag if specified
if ($SelfContained) {
    $buildArgs += '--self-contained'
    $buildArgs += 'true'
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Err "Build failed"
    exit $LASTEXITCODE
}

Write-Success "Build complete"

# ==============================================================================
# Publish (if requested)
# ==============================================================================

if ($Publish) {
    Write-Info "Publishing application..."

    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    }

    $publishArgs = @(
        'publish',
        $MainProject,
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--output', $OutputPath,
        '--verbosity', 'minimal'
    )

    if ($SelfContained) {
        $publishArgs += '--self-contained'
        $publishArgs += 'true'
    }
    else {
        $publishArgs += '--self-contained'
        $publishArgs += 'false'
    }

    if ($SingleFile) {
        $publishArgs += '-p:PublishSingleFile=true'
        $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
        $publishArgs += '-p:EnableCompressionInSingleFile=true'
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Publish failed"
        exit $LASTEXITCODE
    }

    # List published files
    Write-Info "Published files:"
    Get-ChildItem -Path $OutputPath -Recurse -File | ForEach-Object {
        $size = "{0:N2} MB" -f ($_.Length / 1MB)
        Write-Info "  $($_.Name) ($size)"
    }

    Write-Success "Published to: $OutputPath"
}

# ==============================================================================
# Summary
# ==============================================================================

Write-Host ""
Write-Success "Build completed successfully!"
Write-Host ""
Write-Info "Next steps:"

if ($Publish) {
    Write-Host "  1. Deploy to Fedora: .\scripts\deploy.ps1 -SourcePath '$OutputPath'" -ForegroundColor White
    Write-Host "  2. Run tests: dotnet test" -ForegroundColor White
}
else {
    Write-Host "  1. Create release: .\scripts\build.ps1 -Publish -SingleFile" -ForegroundColor White
    Write-Host "  2. Run tests: dotnet test" -ForegroundColor White
    Write-Host "  3. Deploy: .\scripts\deploy.ps1" -ForegroundColor White
}

exit 0
