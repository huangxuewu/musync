param([switch]$Publish, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    $sdkVersion = '10.0.401'
    $sdkPath = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $sdkPath)) {
        $archive = Join-Path $taskRoot '.tools\dotnet-sdk-10.0.401-win-x64.zip'
        New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
        if (-not (Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" -OutFile $archive
        }
        $expected = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $expected) { throw 'SDK checksum mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath (Split-Path $sdkPath) -Force
    }
    $nodeVersion = '24.21.0'
    $nodeDirectory = Join-Path $taskRoot ".tools\node\node-v$nodeVersion-win-x64"
    if (-not (Test-Path -LiteralPath (Join-Path $nodeDirectory 'node.exe'))) {
        $archive = Join-Path $taskRoot ".tools\node-v$nodeVersion-win-x64.zip"
        if (-not (Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest "https://nodejs.org/dist/v$nodeVersion/node-v$nodeVersion-win-x64.zip" -OutFile $archive
        }
        $expected = '158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541'
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Node checksum mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $taskRoot '.tools\node') -Force
    }
    $node = Join-Path $nodeDirectory 'node.exe'
    $npm = Join-Path $nodeDirectory 'node_modules\npm\bin\npm-cli.js'
    & $node $npm ci --prefix src/Musync.AirPlayBridge --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'AirPlay dependencies failed to install.' }
    & $sdkPath build Musync.sln -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $sdkPath test tests/Musync.Tests/Musync.Tests.csproj -c Release --no-build --nologo -v minimal --logger 'trx;LogFileName=musync-tests.trx'
        if ($LASTEXITCODE -ne 0) { throw 'C# tests failed.' }
        & $sdkPath test tests/Musync.App.Tests/Musync.App.Tests.csproj -c Release --no-build --nologo -v minimal --logger 'trx;LogFileName=musync-app-tests.trx'
        if ($LASTEXITCODE -ne 0) { throw 'App tests failed.' }
        & $node --test src/Musync.AirPlayBridge/test.mjs
        if ($LASTEXITCODE -ne 0) { throw 'AirPlay bridge tests failed.' }
    }
    if ($Publish) {
        [xml]$properties = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw
        $version = [string]$properties.Project.PropertyGroup.Version
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
        $destination = [IO.Path]::GetFullPath((Join-Path $artifactRoot "Musync-$version-win-x64"))
        if (-not $destination.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Publish directory must remain inside artifacts.' }
        if (Test-Path -LiteralPath $destination) {
            if (Get-Process -Name Musync -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($destination + [IO.Path]::DirectorySeparatorChar) }) { throw 'Close this version of Musync before rebuilding its package.' }
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
        & $sdkPath publish src/Musync.App/Musync.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:IncludeSourceRevisionInInformationalVersion=false -o $destination --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        $bridgeDestination = Join-Path $destination 'AirPlayBridge'
        New-Item -ItemType Directory -Force $bridgeDestination | Out-Null
        Copy-Item -LiteralPath 'src\Musync.AirPlayBridge\bridge.mjs','src\Musync.AirPlayBridge\receiver-sessions.mjs','src\Musync.AirPlayBridge\package.json','src\Musync.AirPlayBridge\package-lock.json' -Destination $bridgeDestination -Force
        Copy-Item -LiteralPath 'src\Musync.AirPlayBridge\node_modules' -Destination $bridgeDestination -Recurse -Force
        Copy-Item -LiteralPath $node -Destination $bridgeDestination -Force
        Copy-Item -LiteralPath (Join-Path $nodeDirectory 'LICENSE') -Destination (Join-Path $bridgeDestination 'NODE-LICENSE.txt') -Force
        & (Join-Path $taskRoot 'tools/Prepare-RadioTools.ps1')
        & (Join-Path $taskRoot 'tools/Copy-RadioTools.ps1') -Destination $destination
        Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.txt','README.md','CHANGELOG.md' -Destination $destination -Force
        Copy-Item -LiteralPath 'docs' -Destination $destination -Recurse -Force
        if (Test-Path -LiteralPath 'release-validation.json') { Copy-Item -LiteralPath 'release-validation.json' -Destination $destination -Force }
        & (Join-Path $taskRoot 'collect-licenses.ps1') -Destination (Join-Path $destination 'Licenses')
        $archive = Join-Path $taskRoot "artifacts\Musync-$version-win-x64.zip"
        Compress-Archive -LiteralPath $destination -DestinationPath $archive -Force
        $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksum + '  ' + (Split-Path -Leaf $archive) | Set-Content -LiteralPath (Join-Path $artifactRoot "Musync-$version-win-x64.sha256") -Encoding ascii
        [pscustomobject]@{
            Version = $version; Platform = 'win-x64'; Executable = "artifacts/Musync-$version-win-x64/Musync.exe"
            Archive = "artifacts/Musync-$version-win-x64.zip"; Bytes = (Get-Item -LiteralPath $archive).Length
            SHA256 = $checksum; SelfContained = $true; Signed = $false; Validation = 'release-validation.json'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'build-manifest.json') -Encoding utf8
        Write-Output "Portable app: $destination\Musync.exe"
        Write-Output "ZIP: $archive"
    }
}
finally { Pop-Location }
