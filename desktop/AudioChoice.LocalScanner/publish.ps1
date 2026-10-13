<#
.SYNOPSIS
    Builds the AudioChoice Local Scanner for distribution to the computer that will run it.

.DESCRIPTION
    Not needed to just run the app -- this is only for producing the files to copy over to a
    new machine in the first place. If AudioChoiceLocalScanner.exe already exists next to
    this script (because someone already copied a built one here), there is nothing to do.

    Run this from the repository, with the .NET 8 SDK installed, then copy the resulting
    publish folder's contents (plus this folder's setup.ps1, python/, and
    appsettings.local.json.example) to the target computer.
#>

$ErrorActionPreference = "Stop"
$scriptRoot = $PSScriptRoot
$outputFolder = Join-Path $scriptRoot "publish"

Write-Host "Publishing AudioChoiceLocalScanner (win-x64, framework-dependent)..."
dotnet publish $scriptRoot -c Release -r win-x64 --self-contained false -o $outputFolder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $scriptRoot "setup.ps1") $outputFolder -Force
Copy-Item (Join-Path $scriptRoot "appsettings.local.json.example") $outputFolder -Force
Copy-Item (Join-Path $scriptRoot "README.md") $outputFolder -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $scriptRoot "python") $outputFolder -Recurse -Force

Write-Host ""
Write-Host "Done. Copy the entire '$outputFolder' folder to the computer that will run scans," -ForegroundColor Green
Write-Host "then run setup.ps1 there once, followed by AudioChoiceLocalScanner.exe." -ForegroundColor Green
