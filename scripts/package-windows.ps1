[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/NxToWin/NxToWin.csproj'
[xml]$metadata = Get-Content -LiteralPath $project -Raw
$version = $metadata.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Set a stable version (major.minor.patch) in NxToWin.csproj.'
}
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $root "src/NxToWin/bin/distribution/$version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
$app = Join-Path $work 'app'
$packages = Join-Path $work 'packages'

Push-Location $root
try {
    dotnet tool restore
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $app
    foreach ($file in @('NxToWin.exe', 'hidapi.dll', 'WinUHid.dll', 'WinUHidDevs.dll', 'Licenses')) {
        if (!(Test-Path -LiteralPath (Join-Path $app $file))) { throw "Missing publish output: $file" }
    }
    dotnet tool run vpk -- pack --packId NxToWin.Desktop --packTitle NxToWin --packAuthors iCosiSenpai `
        --packVersion $version --runtime win-x64 --packDir $app --outputDir $packages --mainExe NxToWin.exe `
        --icon (Join-Path $root 'src/NxToWin/Assets/app.ico') --instLocation PerUser --delta None --noPortable

    $setup = @(Get-ChildItem -LiteralPath $packages -Filter '*-Setup.exe' -File)
    if ($setup.Count -ne 1) { throw 'Expected one Windows Setup executable.' }
    $assetNames = @('NxToWin-Setup.exe', "NxToWin.Desktop-$version-full.nupkg")
    Copy-Item -LiteralPath $setup[0].FullName -Destination (Join-Path $OutputDirectory $assetNames[0]) -Force
    Copy-Item -LiteralPath (Join-Path $packages $assetNames[1]) -Destination $OutputDirectory -Force

    $checksums = foreach ($name in $assetNames) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name
    }
    $checksums | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Windows release $version ready in $OutputDirectory"
}
finally {
    Pop-Location
}
