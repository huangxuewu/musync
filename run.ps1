$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$portable = Join-Path $taskRoot "artifacts\Musync-$version-win-x64\Musync.exe"
if (Test-Path -LiteralPath $portable) {
    Start-Process -FilePath $portable -WorkingDirectory (Split-Path $portable) -WindowStyle Hidden
}
else {
    $sdk = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $sdk)) { throw 'Run build.ps1 first.' }
    Push-Location -LiteralPath $taskRoot
    try {
        & $sdk build src/Musync.App/Musync.App.csproj -c Release --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        $dll = Join-Path $taskRoot 'src\Musync.App\bin\Release\net10.0\Musync.dll'
        Start-Process -FilePath $sdk -ArgumentList ('"' + $dll + '"') -WorkingDirectory $taskRoot -WindowStyle Hidden
    }
    finally { Pop-Location }
}
