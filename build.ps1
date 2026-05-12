#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string[]]$Runtimes = @("win-x64", "linux-x64"),
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$RepoRoot    = $PSScriptRoot
$ProjectDir  = Join-Path $RepoRoot "EmailToDiscord"
$ProjectFile = Join-Path $ProjectDir "EmailToDiscord.csproj"
$ReleaseDir  = Join-Path $RepoRoot "release"
$StagingRoot = Join-Path $ReleaseDir "staging"

if (-not (Test-Path $ProjectFile)) {
    throw "Project file not found at $ProjectFile"
}

if ($Clean -and (Test-Path $ReleaseDir)) {
    Write-Host "Cleaning $ReleaseDir"
    Remove-Item -Recurse -Force $ReleaseDir
}

New-Item -ItemType Directory -Force -Path $ReleaseDir  | Out-Null
New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null

foreach ($rid in $Runtimes) {
    Write-Host ""
    Write-Host "==> Publishing $rid (framework-dependent)"

    $stageDir = Join-Path $StagingRoot "EmailToDiscord-$Version-$rid"
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }

    & dotnet publish $ProjectFile `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained false `
        --output $stageDir `
        /p:Version=$Version `
        /p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $exampleConfig = Join-Path $ProjectDir "config.example.yaml"
    if (Test-Path $exampleConfig) {
        Copy-Item $exampleConfig (Join-Path $stageDir "config.example.yaml") -Force
    }
    Remove-Item -Force          (Join-Path $stageDir "config.yaml") -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $stageDir "data")        -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force (Join-Path $stageDir "logs")        -ErrorAction SilentlyContinue

    if ($rid -like "win-*") {
        $archive = Join-Path $ReleaseDir "EmailToDiscord-$Version-$rid.zip"
        if (Test-Path $archive) { Remove-Item -Force $archive }
        Write-Host "==> Packaging $archive"
        Compress-Archive -Path $stageDir -DestinationPath $archive -CompressionLevel Optimal
    } else {
        $archiveName = "EmailToDiscord-$Version-$rid.tar.gz"
        $archive = Join-Path $ReleaseDir $archiveName
        if (Test-Path $archive) { Remove-Item -Force $archive }
        Write-Host "==> Packaging $archive"
        Push-Location $StagingRoot
        try {
            & tar -czf "../$archiveName" (Split-Path $stageDir -Leaf)
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        } finally {
            Pop-Location
        }
    }
}

Remove-Item -Recurse -Force $StagingRoot

Write-Host ""
Write-Host "Done. Artifacts in $ReleaseDir`:"
Get-ChildItem $ReleaseDir | ForEach-Object { Write-Host "  $($_.Name)" }
