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
Copy-Item -LiteralPath (Join-Path $taskRoot 'third-party') -Destination $Destination -Recurse -Force
$runtime = Join-Path $taskRoot '.tools\dotnet'
Get-ChildItem -LiteralPath $runtime -File | Where-Object Name -Match 'licen[cs]e|notice' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination ('DOTNET-' + $_.Name)) -Force
}
