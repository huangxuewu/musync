param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$sdk = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
$packageJson = & $sdk list (Join-Path $taskRoot 'src\Musync.App\Musync.App.csproj') package --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate dependency licenses.' }
$packages = ($packageJson | ConvertFrom-Json).projects[0].frameworks[0]
$records = [System.Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Force $Destination | Out-Null
foreach ($package in (@($packages.topLevelPackages) + @($packages.transitivePackages))) {
    $packageRoot = Join-Path "$env:USERPROFILE\.nuget\packages" ($package.id.ToLowerInvariant() + '\' + $package.resolvedVersion)
    $specFile = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' | Select-Object -First 1
    if ($null -eq $specFile) { throw "Package metadata missing: $($package.id)" }
    [xml]$spec = Get-Content -LiteralPath $specFile.FullName -Raw
    $metadata = $spec.package.metadata
    $records.Add([pscustomobject]@{Name=$package.id; Version=$package.resolvedVersion; Authors=[string]$metadata.authors; Copyright=[string]$metadata.copyright; License=[string]$metadata.license.InnerText; LicenseUrl=[string]$metadata.licenseUrl; Project=[string]$metadata.projectUrl})
    $licenseFiles = Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match 'licen[cs]e|notice|copyright'
    if ($licenseFiles.Count -gt 0) {
        $target = Join-Path $Destination ($package.id + '-' + $package.resolvedVersion)
        New-Item -ItemType Directory -Force $target | Out-Null
        $licenseFiles | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
    }
}
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'nuget-dependencies.json') -Encoding utf8
$taskNotices = Join-Path $Destination 'third-party'
New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'third-party') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $taskNotices -Force
}
# Source dependencies retain their license and provenance without copying
# compiled artifacts, sources or obj directories into a portable release.
$taskCastSource = Join-Path $taskRoot 'third-party/SharpCaster'
$taskCastNotices = Join-Path $taskNotices 'SharpCaster'
New-Item -ItemType Directory -Path $taskCastNotices -Force | Out-Null
foreach ($taskNotice in @('LICENSE.txt', 'README.md', 'UPSTREAM.json')) {
    Copy-Item -LiteralPath (Join-Path $taskCastSource $taskNotice) -Destination $taskCastNotices -Force
}
$taskCastPatches = Join-Path $taskCastNotices 'patches'
New-Item -ItemType Directory -Path $taskCastPatches -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskCastSource 'patches/0001-forward-all-media-status.patch') -Destination $taskCastPatches -Force
@{
    Name = 'SharpCaster'; UpstreamVersion = '3.0.0'; UpstreamCommit = 'd04aa95f8a518c6b0434012e3feee7ce3f36ef61'
    LocalPatch = 'forward-all-media-status'; License = 'MIT'; Authors = 'Teemu Tapanila'
    Project = 'https://github.com/Tapanila/SharpCaster'; Notice = 'third-party/SharpCaster/LICENSE.txt'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'source-dependencies.json') -Encoding utf8
$runtime = Join-Path $taskRoot '.tools\dotnet'
Get-ChildItem -LiteralPath $runtime -File | Where-Object Name -Match 'licen[cs]e|notice' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination ('DOTNET-' + $_.Name)) -Force
}
