<#
.SYNOPSIS
    One-time setup for the AudioChoice Local Scanner on this computer.

.DESCRIPTION
    Installs everything AudioChoiceLocalScanner.exe needs that cannot be bundled into the
    published app itself:
      - The .NET 8 runtime (the app references AudioChoice.Api, itself an ASP.NET project,
        which .NET's own tooling will not let a self-contained single-file exe reference --
        see AudioChoice.LocalScanner.csproj's own remarks -- so this app is published
        framework-dependent instead, and needs the runtime present on this machine).
      - Python, in a private virtual environment under this folder (never touching any other
        Python install on this computer, and never requiring one to already exist beyond
        whatever the winget/python.org installer below sets up).
      - faster-whisper and its dependencies, installed into that virtual environment.
      - The whisper model weights themselves, downloaded once now rather than silently during
        someone's first real scan.

    Safe to re-run: every step below checks whether its own work is already done before
    doing it again.

.NOTES
    Run this from an ordinary PowerShell window (administrator rights are not required
    unless winget itself asks for them to install something system-wide).
#>

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Test-CommandExists($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

# --- .NET 8 runtime -----------------------------------------------------------------------
Write-Step ".NET 8 runtime"
$dotnetRuntimes = @()
if (Test-CommandExists "dotnet") {
    $dotnetRuntimes = (& dotnet --list-runtimes 2>$null)
}
if ($dotnetRuntimes -match "Microsoft\.NETCore\.App 8\.") {
    Write-Host "Already installed."
} else {
    Write-Host "Not found. Installing the .NET 8 Desktop/Runtime via winget..."
    if (-not (Test-CommandExists "winget")) {
        throw "winget is not available on this computer. Install the .NET 8 Runtime manually " +
              "from https://dotnet.microsoft.com/download/dotnet/8.0 (the 'Runtime' column, " +
              "x64), then re-run this script."
    }
    winget install --id Microsoft.DotNet.Runtime.8 --silent --accept-package-agreements --accept-source-agreements
    Write-Host "Installed. You may need to open a new PowerShell window for PATH changes to take effect."
}

# --- Python --------------------------------------------------------------------------------
Write-Step "Python"
$pythonCommand = $null
foreach ($candidate in @("python", "python3")) {
    if (Test-CommandExists $candidate) {
        $versionOutput = (& $candidate --version) 2>&1
        if ($versionOutput -match "Python 3\.(1[0-9]|[9])") {
            $pythonCommand = $candidate
            break
        }
    }
}
if (-not $pythonCommand) {
    Write-Host "Python 3.9+ not found. Installing Python via winget..."
    if (-not (Test-CommandExists "winget")) {
        throw "winget is not available on this computer. Install Python 3.11+ manually from " +
              "https://www.python.org/downloads/ (check 'Add python.exe to PATH' during setup), " +
              "then re-run this script."
    }
    winget install --id Python.Python.3.12 --silent --accept-package-agreements --accept-source-agreements
    Write-Host "Installed. Open a new PowerShell window and re-run this script so PATH picks up python."
    exit 0
}
Write-Host "Using $pythonCommand ($(& $pythonCommand --version))."

# --- Virtual environment + faster-whisper --------------------------------------------------
Write-Step "Python virtual environment"
$venvPath = Join-Path $scriptRoot "whisper-venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
if (Test-Path $venvPython) {
    Write-Host "Already created at $venvPath."
} else {
    & $pythonCommand -m venv $venvPath
    Write-Host "Created at $venvPath."
}

Write-Step "faster-whisper"
$installed = & $venvPython -m pip show faster-whisper 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Already installed."
} else {
    Write-Host "Installing faster-whisper (this downloads several hundred MB of dependencies)..."
    & $venvPython -m pip install --upgrade pip --quiet
    & $venvPython -m pip install faster-whisper --quiet
    Write-Host "Installed."
}

# --- Model weights ---------------------------------------------------------------------------
Write-Step "Whisper model weights"
$whisperModel = if ($env:AUDIOCHOICE_WHISPER_MODEL) { $env:AUDIOCHOICE_WHISPER_MODEL } else { "large-v3-turbo" }
Write-Host "Downloading/verifying '$whisperModel' (this can take a while on the first run)..."
& $venvPython (Join-Path $scriptRoot "python\transcribe.py") --warm-up --model $whisperModel --device cpu --compute-type int8
if ($LASTEXITCODE -ne 0) {
    throw "Failed to download or load the whisper model. See the error above."
}

# --- ffmpeg ----------------------------------------------------------------------------------
Write-Step "ffmpeg"
if (Test-CommandExists "ffmpeg" -and Test-CommandExists "ffprobe") {
    Write-Host "Already installed."
} else {
    Write-Host "ffmpeg/ffprobe not found. Installing via winget..."
    if (-not (Test-CommandExists "winget")) {
        throw "winget is not available on this computer. Install ffmpeg manually from " +
              "https://ffmpeg.org/download.html and ensure ffmpeg.exe/ffprobe.exe are on " +
              "PATH, then re-run this script."
    }
    winget install --id Gyan.FFmpeg --silent --accept-package-agreements --accept-source-agreements
    Write-Host "Installed. Open a new PowerShell window so PATH picks up ffmpeg."
}

# --- API keys ----------------------------------------------------------------------------------
Write-Step "API keys for content analysis"
$configPath = Join-Path $scriptRoot "appsettings.local.json"
if (Test-Path $configPath) {
    Write-Host "appsettings.local.json already exists -- leaving it as-is. Edit it directly " +
               "to change model tiers or keys."
} else {
    Write-Host "No appsettings.local.json found yet."
    Write-Host "Create one from appsettings.local.json.example, fill in your Gemini and/or " +
               "DeepInfra API key, and you're ready to scan. See README.md for where to get " +
               "those keys and roughly what they cost per book."
}

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "Run a scan with:"
Write-Host "  .\AudioChoiceLocalScanner.exe `"C:\path\to\your\audiobook.m4b`""
