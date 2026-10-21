<#
.SYNOPSIS
    One-time setup for the AudioChoice Local Scanner on this computer.

.DESCRIPTION
    Installs everything AudioChoiceLocalScanner.exe needs that cannot be bundled into the
    published app itself, and builds the app from source:
      - The .NET 8 SDK (not just the runtime -- this script builds the app itself, from the
        source in this repository, rather than requiring a separately published binary to be
        copied over). The app references AudioChoice.Api, itself an ASP.NET project, which
        .NET's own tooling will not let a self-contained single-file exe reference -- see
        AudioChoice.LocalScanner.csproj's own remarks -- so it is published framework-
        dependent, and needs the SDK's bundled runtime present at run time too.
      - Python, in a private virtual environment inside the published app's own folder (never
        touching any other Python install on this computer).
      - faster-whisper and its dependencies, installed into that virtual environment.
      - The whisper model weights themselves, downloaded once now rather than silently during
        someone's first real scan.
      - ffmpeg, which the scan pipeline needs to split an audiobook into chunks.
      - Builds AudioChoiceLocalScanner.exe itself, from this folder's own source, into
        .\publish.

    Safe to re-run: every step below checks whether its own work is already done before
    doing it again.

.NOTES
    Run this from an ordinary PowerShell window (administrator rights are not required
    unless winget itself asks for them to install something system-wide). After winget
    installs something new, PATH does not update in the current window -- the script exits
    and asks for a new one specifically for that reason, rather than silently failing a few
    steps later when the newly-installed command still can't be found.
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

function Request-NewWindow($reason) {
    Write-Host ""
    Write-Host "$reason" -ForegroundColor Yellow
    Write-Host "Open a NEW PowerShell window (this one will not see the PATH change) and " -ForegroundColor Yellow
    Write-Host "run .\setup.ps1 again from there to continue." -ForegroundColor Yellow
    exit 0
}

# --- .NET 8 SDK -------------------------------------------------------------------------------
Write-Step ".NET 8 SDK"
$dotnetSdks = @()
if (Test-CommandExists "dotnet") {
    $dotnetSdks = (& dotnet --list-sdks 2>$null)
}
if ($dotnetSdks -match "^8\.") {
    Write-Host "Already installed."
} else {
    Write-Host "Not found. Installing the .NET 8 SDK via winget..."
    if (-not (Test-CommandExists "winget")) {
        throw "winget is not available on this computer. Install the .NET 8 SDK manually " +
              "from https://dotnet.microsoft.com/download/dotnet/8.0 (the 'SDK' column, x64), " +
              "then re-run this script."
    }
    winget install --id Microsoft.DotNet.SDK.8 --silent --accept-package-agreements --accept-source-agreements
    Write-Host "Installed."
    Request-NewWindow "The .NET SDK was just installed."
}

# --- Python ------------------------------------------------------------------------------------
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
    Write-Host "Installed."
    Request-NewWindow "Python was just installed."
}
Write-Host "Using $pythonCommand ($(& $pythonCommand --version))."

# --- ffmpeg ------------------------------------------------------------------------------------
Write-Step "ffmpeg"
$hasFfmpeg = (Test-CommandExists "ffmpeg") -and (Test-CommandExists "ffprobe")
if ($hasFfmpeg) {
    Write-Host "Already installed."
} else {
    Write-Host "ffmpeg/ffprobe not found. Installing via winget..."
    if (-not (Test-CommandExists "winget")) {
        throw "winget is not available on this computer. Install ffmpeg manually from " +
              "https://ffmpeg.org/download.html and ensure ffmpeg.exe/ffprobe.exe are on " +
              "PATH, then re-run this script."
    }
    winget install --id Gyan.FFmpeg --silent --accept-package-agreements --accept-source-agreements
    Write-Host "Installed."
    Request-NewWindow "ffmpeg was just installed."
}

# --- Build the app ------------------------------------------------------------------------------
Write-Step "Building AudioChoiceLocalScanner"
$publishFolder = Join-Path $scriptRoot "publish"
dotnet publish $scriptRoot -c Release -r win-x64 --self-contained false -o $publishFolder
if ($LASTEXITCODE -ne 0) {
    throw "Build failed. See the error above."
}
Copy-Item (Join-Path $scriptRoot "python") $publishFolder -Recurse -Force
Write-Host "Built to $publishFolder."

# --- Virtual environment + faster-whisper -------------------------------------------------------
# Created directly inside the publish folder, not next to this script: Program.cs looks for
# whisper-venv right next to the running executable (see its own remarks on
# defaultVenvPython), so the venv has to live where the app actually runs from, not where its
# source happens to be checked out.
Write-Step "Python virtual environment"
$venvPath = Join-Path $publishFolder "whisper-venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
if (Test-Path $venvPython) {
    Write-Host "Already created at $venvPath."
} else {
    & $pythonCommand -m venv $venvPath
    Write-Host "Created at $venvPath."
}

Write-Step "faster-whisper"
& $venvPython -m pip show faster-whisper 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Already installed."
} else {
    Write-Host "Installing faster-whisper (this downloads several hundred MB of dependencies)..."
    & $venvPython -m pip install --upgrade pip --quiet
    & $venvPython -m pip install faster-whisper --quiet
    Write-Host "Installed."
}

# --- Model weights -------------------------------------------------------------------------------
Write-Step "Whisper model weights"
$whisperModel = if ($env:AUDIOCHOICE_WHISPER_MODEL) { $env:AUDIOCHOICE_WHISPER_MODEL } else { "large-v3-turbo" }
Write-Host "Downloading/verifying '$whisperModel' (this can take a while on the first run)..."
& $venvPython (Join-Path $publishFolder "python\transcribe.py") --warm-up --model $whisperModel --device cpu --compute-type int8
if ($LASTEXITCODE -ne 0) {
    throw "Failed to download or load the whisper model. See the error above."
}

# --- API keys ---------------------------------------------------------------------------------
Write-Step "API keys for content analysis"
$exampleConfigPath = Join-Path $scriptRoot "appsettings.local.json.example"
$configPath = Join-Path $publishFolder "appsettings.local.json"
if (Test-Path $configPath) {
    Write-Host "appsettings.local.json already exists in the publish folder -- leaving it " +
               "as-is. Edit it directly there to change model tiers or keys."
} else {
    Copy-Item $exampleConfigPath $configPath
    Write-Host "Created $configPath from the example template."
    Write-Host "Open it and fill in your Gemini and/or DeepInfra API key before running a " +
               "scan. See README.md for where to get those keys and roughly what they cost " +
               "per book."
}

Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "Fill in appsettings.local.json (see above) if you have not already, then run a scan with:"
Write-Host "  cd `"$publishFolder`""
Write-Host "  .\AudioChoiceLocalScanner.exe `"C:\path\to\your\audiobook.m4b`""
