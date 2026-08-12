param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name NuclearOption -ErrorAction SilentlyContinue) {
    throw 'Close Nuclear Option before installing the mod.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'artifacts\blacknight2u.sarhcontrol'
$destination = Join-Path $GameDir 'BepInEx\plugins\blacknight2u.sarhcontrol'

if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build-Package.ps1') -GameDir $GameDir
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
elseif (!(Test-Path -LiteralPath (Join-Path $source 'SARHControl.dll'))) {
    throw 'No packaged SARHControl.dll exists. Run without -SkipBuild first.'
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
Write-Host "Installed Radar Missile Control to $destination"
