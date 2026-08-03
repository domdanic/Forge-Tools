param(
    [Parameter(Mandatory = $true)][string]$PluginDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PluginDirectory).Path
$manifest = Join-Path $source 'plugin.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw 'plugin.json is required at the package root.' }
$parsed = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($parsed.id) -or [string]::IsNullOrWhiteSpace($parsed.version)) { throw 'plugin.json requires id and version.' }
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
Compress-Archive -Path (Join-Path $source '*') -DestinationPath $destination -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
[pscustomobject]@{ Package = $destination; Id = $parsed.id; Version = $parsed.version; Sha256 = $hash } | Format-List
