param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'SARHControl.csproj'
$version = '0.4.1'
$modId = 'blacknight2u.sarhcontrol'
$displayName = 'Radar Missile Control'
$description = 'Host-controlled radar missile difficulty mod. Affects only AI-launched SARH and ARH missiles targeting human pilots; player weapons remain vanilla. Press F8 as host to cycle OFF (VANILLA), NERFED (50%), and DISABLED. Status text appears only when toggled.'
$tags = @('gameplay', 'difficulty', 'sarh', 'arh', 'host-only', 'multiplayer')
$authors = @('blacknight2u')
$githubOwner = 'blacknight2u'
$githubRepoName = 'NuclearOption-RadarMissileControl'
$repoUrl = "https://github.com/$githubOwner/$githubRepoName"
$releaseTag = "v$version"
$releaseFileName = "SARHControl-v$version.zip"
$downloadUrl = "$repoUrl/releases/download/$releaseTag/$releaseFileName"
$urls = @(
    [ordered]@{
        name = 'info'
        url = $repoUrl
    }
)
$artifacts = Join-Path $projectRoot 'artifacts'
$staging = Join-Path $artifacts $modId
$zipPath = Join-Path $artifacts $releaseFileName

dotnet build $project -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$dll = Join-Path $projectRoot "bin\$Configuration\SARHControl.dll"
Copy-Item -LiteralPath $dll -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $staging
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $staging

$meta = [ordered]@{
    id = $modId
    displayName = $displayName
    description = $description
    tags = $tags
    urls = $urls
    authors = $authors
    artifact = [ordered]@{
        fileName = $releaseFileName
        version = $version
        category = 'release'
        type = 'plugin'
        gameVersion = '0.33.4'
        downloadUrl = $downloadUrl
        hash = $null
        extends = $null
        dependencies = @()
        incompatibilities = @()
    }
}
$meta | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $staging 'meta.json') -Encoding utf8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    id = $modId
    displayName = $displayName
    description = $description
    tags = $tags
    urls = $urls
    authors = $authors
    githubOwner = $githubOwner
    githubRepoName = $githubRepoName
    autoUpdateArtifacts = 'True'
    artifacts = @(
        [ordered]@{
            fileName = $releaseFileName
            version = $version
            category = 'release'
            type = 'plugin'
            gameVersion = '0.33.4'
            downloadUrl = $downloadUrl
            hash = "sha256:$zipHash"
            dependencies = @()
            incompatibilities = @()
        }
    )
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'manifest-entry.example.json') -Encoding utf8
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts "$modId.json") -Encoding utf8

Write-Host "Package: $zipPath"
Write-Host "SHA-256: $zipHash"
Write-Host "NOMM manifest template: $(Join-Path $artifacts 'manifest-entry.example.json')"
Write-Host "NOMNOM submission file: $(Join-Path $artifacts "$modId.json")"
