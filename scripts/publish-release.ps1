[CmdletBinding()]
param(
    [string]$Version = "v0.1.0",
    [string[]]$RuntimeIdentifiers = @("win-x64", "win-x86")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "PMCL.csproj"
$license = Join-Path $root "LICENSE"
$notice = Join-Path $root "NOTICE"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

if ($Version -notmatch '^v?(?<number>\d+\.\d+\.\d+)$') {
    throw "Version must look like v0.1.0 or 0.1.0."
}

$versionNumber = $Matches["number"]
$tag = "v$versionNumber"

[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$projectVersion = @($projectXml.Project.PropertyGroup.Version) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1

if (-not [string]::Equals($projectVersion, $versionNumber, [StringComparison]::Ordinal)) {
    throw "Version mismatch: requested $versionNumber, but PMCL.csproj declares '$projectVersion'. Update <Version> before releasing."
}

$supportedRuntimeIdentifiers = @("win-x64", "win-x86")
foreach ($rid in $RuntimeIdentifiers) {
    if ($rid -notin $supportedRuntimeIdentifiers) {
        throw "Unsupported runtime identifier '$rid'. Supported values: $($supportedRuntimeIdentifiers -join ', ')."
    }
}

$releaseRoot = Join-Path $root "artifacts\release\$tag"
$publishRoot = Join-Path $root "artifacts\publish\$tag"

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

$packages = @()

foreach ($rid in $RuntimeIdentifiers) {
    $publishDir = Join-Path $publishRoot $rid
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Write-Host ""
    Write-Host "Publishing PMCL $tag for $rid..." -ForegroundColor Cyan

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $publishDir `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$versionNumber `
        -p:AssemblyVersion="$versionNumber.0" `
        -p:FileVersion="$versionNumber.0" `
        -p:InformationalVersion=$versionNumber

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit code $LASTEXITCODE)."
    }

    $executable = Join-Path $publishDir "PMCL.exe"
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Expected single-file executable was not produced: $executable"
    }

    $unexpected = Get-ChildItem -LiteralPath $publishDir -File |
        Where-Object { $_.Name -notmatch '^(PMCL\.exe|.+\.pdb)$' }
    if ($unexpected) {
        $names = ($unexpected | Select-Object -ExpandProperty Name) -join ", "
        throw "Single-file publish left unexpected files in ${rid}: $names"
    }

    $stage = Join-Path $releaseRoot $rid
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath $executable -Destination $stage
    Copy-Item -LiteralPath $license -Destination $stage
    Copy-Item -LiteralPath $notice -Destination $stage

    $zipName = "PMCL-$tag-$rid.zip"
    $zipPath = Join-Path $releaseRoot $zipName
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashPath = "$zipPath.sha256"
    "$hash  $zipName" | Set-Content -LiteralPath $hashPath -Encoding Ascii

    $packages += $zipPath
    $packages += $hashPath
    Remove-Item -LiteralPath $stage -Recurse -Force
    Write-Host "Created $zipPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Release $tag is ready:" -ForegroundColor Green
$packages | ForEach-Object { Write-Host "  $_" }
