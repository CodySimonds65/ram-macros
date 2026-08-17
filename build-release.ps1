$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stage = Join-Path $out 'stage'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'ram-macros.csproj') -c Release -r win-x64 --self-contained false -o $stage
Copy-Item (Join-Path $PSScriptRoot 'plugin.json') (Join-Path $stage 'plugin.json') -Force
$zip = Join-Path $out 'plugin.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content (Join-Path $out 'plugin.sha256') -NoNewline
if (-not $env:RAM_PLUGIN_SIGNING_KEY) { throw 'Set RAM_PLUGIN_SIGNING_KEY to an Ed25519 private PEM before creating an official package.' }
openssl pkeyutl -sign -inkey $env:RAM_PLUGIN_SIGNING_KEY -in $zip -out (Join-Path $out 'plugin.sig')
Copy-Item (Join-Path $PSScriptRoot 'plugin.json') (Join-Path $out 'plugin.json') -Force
