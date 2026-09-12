param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'staging/nut')
)

$ErrorActionPreference = 'Stop'
$version = '2.8.5-1-fixNSS'
$archiveName = "NUT-for-Windows-x86_64-RELEASE-$version.7z"
$downloadUrl = "https://www.networkupstools.org/package/windows/$archiveName"
$expectedSha256 = 'a226b9e402e589f3d14118d8d07ef2cdb4e28bc3e30d55de47fef46abdf4dfb0'
$copyingUrl = 'https://raw.githubusercontent.com/networkupstools/nut/v2.8.5/COPYING'
$copyingSha256 = 'a6c3d535c77fbfcecb33a3545668cfa0354a5ad517930c255eb39f135f7213db'
$gplUrl = 'https://raw.githubusercontent.com/networkupstools/nut/v2.8.5/LICENSE-GPL2'
$gplSha256 = 'ab15fd526bd8dd18a9e77ebc139656bf4d33e97fc7238cd11bf60e2b9b8666c6'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "EatonUsbController-NUT-$([Guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $temporaryRoot $archiveName
$extractPath = Join-Path $temporaryRoot 'extract'

function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            return [BitConverter]::ToString(
                $sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

try {
    New-Item $temporaryRoot -ItemType Directory -Force | Out-Null
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath

    $actualSha256 = Get-Sha256 $archivePath
    if ($actualSha256 -ne $expectedSha256) {
        throw "NUT archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
    }

    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip) {
        $sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
    }
    if (-not (Test-Path $sevenZip)) {
        throw '7-Zip is required to prepare the NUT Windows binaries.'
    }

    & $sevenZip x $archivePath "-o$extractPath" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract the NUT archive.' }

    $runtimeRoot = Get-ChildItem $extractPath -Directory -Recurse |
        Where-Object {
            (Test-Path (Join-Path $_.FullName 'bin')) -and
            (Test-Path (Join-Path $_.FullName 'sbin'))
        } |
        Select-Object -First 1
    if (-not $runtimeRoot) { throw 'The NUT archive does not contain bin and sbin directories.' }

    $sourceBin = Join-Path $runtimeRoot.FullName 'bin'
    $sourceSbin = Join-Path $runtimeRoot.FullName 'sbin'
    $requiredFiles = @{
        'upsc.exe' = Join-Path $sourceBin 'upsc.exe'
        'upscmd.exe' = Join-Path $sourceBin 'upscmd.exe'
        'upsrw.exe' = Join-Path $sourceBin 'upsrw.exe'
        'usbhid-ups.exe' = Join-Path $sourceBin 'usbhid-ups.exe'
        'upsd.exe' = Join-Path $sourceSbin 'upsd.exe'
        'upsdrvctl.exe' = Join-Path $sourceSbin 'upsdrvctl.exe'
    }
    foreach ($requiredFile in $requiredFiles.GetEnumerator()) {
        if (-not (Test-Path $requiredFile.Value)) {
            throw "The NUT archive does not contain $($requiredFile.Key)."
        }
    }

    if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
    $clientDir = New-Item (Join-Path $OutputDirectory 'bin') -ItemType Directory -Force
    $serverDir = New-Item (Join-Path $OutputDirectory 'sbin') -ItemType Directory -Force
    $configDir = New-Item (Join-Path $OutputDirectory 'etc') -ItemType Directory -Force

    Copy-Item (Join-Path $sourceBin '*.dll') $clientDir.FullName
    Copy-Item (Join-Path $sourceBin '*.dll') $serverDir.FullName
    Copy-Item (Join-Path $sourceSbin '*.dll') $serverDir.FullName -Force
    foreach ($name in @('upsc.exe', 'upscmd.exe', 'upsrw.exe')) {
        Copy-Item $requiredFiles[$name] $clientDir.FullName
    }
    foreach ($name in @('upsd.exe', 'upsdrvctl.exe', 'usbhid-ups.exe')) {
        Copy-Item $requiredFiles[$name] $serverDir.FullName
    }
    Copy-Item (Join-Path $PSScriptRoot 'nut-config/*') $configDir.FullName

    $copyingPath = Join-Path $OutputDirectory 'NUT-COPYING.txt'
    $gplPath = Join-Path $OutputDirectory 'NUT-LICENSE-GPL2.txt'
    Invoke-WebRequest -Uri $copyingUrl -OutFile $copyingPath
    Invoke-WebRequest -Uri $gplUrl -OutFile $gplPath
    if ((Get-Sha256 $copyingPath) -ne $copyingSha256) {
        throw 'NUT COPYING file checksum mismatch.'
    }
    if ((Get-Sha256 $gplPath) -ne $gplSha256) {
        throw 'NUT GPLv2 license checksum mismatch.'
    }
    Set-Content (Join-Path $OutputDirectory 'NUT-SOURCE.txt') -Encoding utf8 -Value @(
        'Network UPS Tools for Windows'
        "Version: $version"
        "Source archive: $downloadUrl"
        "SHA-256: $expectedSha256"
        "License manifest: $copyingUrl"
        "GPLv2 text: $gplUrl"
        'Project: https://networkupstools.org/'
    )
}
finally {
    if (Test-Path $temporaryRoot) { Remove-Item $temporaryRoot -Recurse -Force }
}