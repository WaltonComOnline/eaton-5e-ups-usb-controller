param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$stagingDir = Join-Path $installerDir 'staging'
$serviceOutput = Join-Path $stagingDir 'service'
$appOutput = Join-Path $stagingDir 'app'

foreach ($output in @($serviceOutput, $appOutput)) {
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    New-Item $output -ItemType Directory -Force | Out-Null
}

& (Join-Path $installerDir 'Prepare-Nut.ps1')

dotnet publish (Join-Path $repoRoot 'src/EatonUsbController.Service/EatonUsbController.Service.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $serviceOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

dotnet publish (Join-Path $repoRoot 'src/EatonUsbController.App/EatonUsbController.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $appOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop app publish failed.' }

Push-Location $installerDir
try {
    $wixArguments = @(
        'build',
        'Package.wxs',
        '-arch', 'x64',
        '-ext', 'WixToolset.UI.wixext',
        '-ext', 'WixToolset.Util.wixext',
        '-d', "ServicePublishDir=$serviceOutput",
        '-d', "AppPublishDir=$appOutput",
        '-d', "NutStagingDir=$(Join-Path $stagingDir 'nut')",
        '-d', "ToolsStagingDir=$(Join-Path $installerDir 'tools')",
        '-d', "IconPath=$(Join-Path $repoRoot 'src/EatonUsbController.App/eaton.ico')",
        '-d', "SetupScript=$(Join-Path $installerDir 'Setup.ps1')",
        '-d', "WinUSBDriverScript=$(Join-Path $installerDir 'Install-WinUSBDriver.ps1')",
        '-sw1101',
        '-o', 'Eaton5EController.msi'
    )

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & wix @wixArguments
        if ($LASTEXITCODE -eq 0) { break }
        if ($attempt -eq 3) { throw 'MSI build failed after 3 attempts.' }

        Write-Warning "WiX build attempt $attempt failed; retrying in 2 seconds."
        Start-Sleep -Seconds 2
    }
}
finally {
    Pop-Location
}

Write-Host "Built $(Join-Path $installerDir 'Eaton5EController.msi')"