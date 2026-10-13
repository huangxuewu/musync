param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDestination = [IO.Path]::GetFullPath($Destination)
if (-not $taskDestination.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The radio bundle destination must remain inside the Musync workspace.'
}
$taskSource = Join-Path $taskRoot '.tools/radio'
$taskFfmpeg = @(Get-ChildItem -LiteralPath (Join-Path $taskSource 'ffmpeg') -Filter ffmpeg.exe -Recurse)
if ($taskFfmpeg.Count -ne 1) { throw 'Prepare the pinned radio tools before packaging.' }
$taskBin = $taskFfmpeg[0].Directory.FullName
$taskOutput = Join-Path $taskDestination 'RadioTools'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskSource 'yt-dlp.exe'),(Join-Path $taskSource 'YT-DLP-LICENSE.txt'),(Join-Path $taskSource 'YT-DLP-THIRD-PARTY.txt') -Destination $taskOutput -Force
Copy-Item -LiteralPath $taskFfmpeg[0].FullName -Destination $taskOutput -Force
Get-ChildItem -LiteralPath $taskBin -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskOutput -Force }
Copy-Item -LiteralPath (Join-Path (Split-Path $taskBin) 'LICENSE.txt') -Destination (Join-Path $taskOutput 'FFMPEG-LICENSE.txt') -Force
if (-not (Test-Path -LiteralPath (Join-Path $taskDestination 'AirPlayBridge/node.exe'))) {
    $taskNode = Join-Path $taskRoot '.tools/node/node-v24.21.0-win-x64'
    Copy-Item -LiteralPath (Join-Path $taskNode 'node.exe') -Destination $taskOutput -Force
    Copy-Item -LiteralPath (Join-Path $taskNode 'LICENSE') -Destination (Join-Path $taskOutput 'NODE-LICENSE.txt') -Force
}
@'
yt-dlp 2026.08.19 (unmodified separate executable):
https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19
https://github.com/yt-dlp/yt-dlp/tree/2026.08.19

FFmpeg n8.1.3-18-g46d899d7a0 (unmodified LGPL shared build):
https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-13-13-05
https://github.com/FFmpeg/FFmpeg/tree/46d899d7a0
https://github.com/BtbN/FFmpeg-Builds
Shared libraries remain replaceable. See FFMPEG-LICENSE.txt for terms.

The Node runtime and its license are in RadioTools or AirPlayBridge.
'@ | Set-Content -LiteralPath (Join-Path $taskOutput 'SOURCES.txt') -Encoding utf8
Write-Output "Radio helpers and licenses copied to $taskOutput"
