$ErrorActionPreference = 'Stop'
$taskVendorRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$taskManifest = Get-Content -LiteralPath (Join-Path $taskVendorRoot 'UPSTREAM.json') -Raw | ConvertFrom-Json -AsHashtable
if ($taskManifest.UpstreamCommit -ne 'd04aa95f8a518c6b0434012e3feee7ce3f36ef61' -or $taskManifest.Files.Count -ne 130) {
    throw 'Update and review the source pin explicitly before changing this dependency.'
}
$taskPatched = 0
foreach ($taskFile in $taskManifest.Files) {
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskVendorRoot $taskFile.Path))
    if (-not $taskPath.StartsWith($taskVendorRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A source path escaped the pinned vendor directory.'
    }
    $taskCanonical = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($taskPath)).Replace("`r`n", "`n")
    $taskHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskCanonical))).ToLowerInvariant()
    $taskExpected = $taskFile.NormalizedSha256
    if ($taskFile.ContainsKey('PatchedNormalizedSha256')) {
        if ($taskFile.Path -ne 'Source/Channels/MediaChannel.cs') { throw 'An undocumented upstream source patch was found.' }
        $taskExpected = $taskFile.PatchedNormalizedSha256; $taskPatched++
    }
    if ($taskHash -ne $taskExpected) { throw ('Pinned source differs from its reviewed hash: ' + $taskFile.Path) }
}
if ($taskPatched -ne 1 -or @(Get-ChildItem -LiteralPath (Join-Path $taskVendorRoot 'Source') -Recurse -File -Filter '*.cs').Count -ne 130) {
    throw 'Unexpected source additions or missing notification patch.'
}
[pscustomobject]@{ UpstreamCommit = $taskManifest.UpstreamCommit; SourceFilesVerified = 130; UnchangedUpstreamFiles = 129; DocumentedPatches = $taskPatched }
