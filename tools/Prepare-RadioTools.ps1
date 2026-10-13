$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDestination = Join-Path $taskRoot '.tools/radio'
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
function Get-VerifiedRadioTool([string]$Uri, [string]$Path, [string]$Sha256) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256) { return }
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) { throw "Integrity check failed: $Path" }
}
Get-VerifiedRadioTool 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe' (Join-Path $taskDestination 'yt-dlp.exe') '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
Get-VerifiedRadioTool 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.08.19/LICENSE' (Join-Path $taskDestination 'YT-DLP-LICENSE.txt') '7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C'
Get-VerifiedRadioTool 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.08.19/THIRD_PARTY_LICENSES.txt' (Join-Path $taskDestination 'YT-DLP-THIRD-PARTY.txt') '472AEFE951C7DB35E1657C1D13FD337140511ED6F2B329205105AD441C5A02B7'
$taskArchive = Join-Path $taskDestination 'ffmpeg-lgpl.zip'
Get-VerifiedRadioTool 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-13-13-05/ffmpeg-n8.1.3-18-g46d899d7a0-win64-lgpl-shared-8.1.zip' $taskArchive '19D92DFB1481E72E4E8F94844BF609418297A646F33DEB6864C409CA752469C4'
Expand-Archive -LiteralPath $taskArchive -DestinationPath (Join-Path $taskDestination 'ffmpeg') -Force
Write-Output 'Radio tools prepared in .tools/radio. Node.js 22+ must be available on PATH or under .tools/node.'
