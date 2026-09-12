param(
    [ValidateSet('Check','Install','Rollback')]
    [string]$Action,
    [string]$ToolsDir
)

$ErrorActionPreference = 'Stop'
$logFile = Join-Path $env:TEMP 'EatonUsbController-WinUSB.log'

function Log($msg) {
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "$ts [WinUSB] $msg" | Tee-Object -FilePath $logFile -Append | Write-Host
}

function Get-UpsDeviceStatus {
    Log "Scanning for Eaton UPS device (VID_0463&PID_FFFF)..."
    
    $pnpOutput = pnputil /enum-devices /connected 2>&1 | Out-String
    
    if ($pnpOutput -notmatch 'VID_0463') {
        Log "Eaton UPS device not found among connected devices"
        return @{ Found = $false; IsWinUSB = $false; InstanceId = $null }
    }
    
    $blocks = $pnpOutput -split '(?=Instance ID:)'
    foreach ($block in $blocks) {
        if ($block -match 'VID_0463&PID_FFFF') {
            $instanceId = if ($block -match 'Instance ID:\s*(.+)') { $Matches[1].Trim() } else { $null }
            $driverName = if ($block -match 'Driver Name:\s*(.+)') { $Matches[1].Trim() } else { '' }
            $className = if ($block -match 'Class Name:\s*(.+)') { $Matches[1].Trim() } else { '' }
            
            $isWinUSB = ($className -match 'USBDevice|Universal Serial Bus devices') -or ($driverName -match 'winusb')
            
            Log "Found device: InstanceId=$instanceId, Class=$className, Driver=$driverName, IsWinUSB=$isWinUSB"
            
            return @{
                Found      = $true
                IsWinUSB   = $isWinUSB
                InstanceId = $instanceId
                ClassName  = $className
                DriverName = $driverName
            }
        }
    }
    
    Log "VID_0463 found in output but no PID_FFFF match in parsed blocks"
    return @{ Found = $false; IsWinUSB = $false; InstanceId = $null }
}

function Invoke-Check {
    Log "=== WinUSB Check ==="
    $status = Get-UpsDeviceStatus
    
    if (-not $status.Found) {
        Log "Result: Device not found (exit 2)"
        exit 2
    }
    
    if ($status.IsWinUSB) {
        Log "Result: Already using WinUSB (exit 0)"
        exit 0
    }
    
    Log "Result: Needs WinUSB driver swap (exit 1)"
    exit 1
}

function Invoke-Install {
    Log "=== WinUSB Install ==="
    
    $preStatus = Get-UpsDeviceStatus
    if (-not $preStatus.Found) {
        Log "WARN: UPS device not found. Cannot install WinUSB driver."
        Log "The user will need to run Zadig manually after connecting the UPS."
        exit 2
    }
    
    if ($preStatus.IsWinUSB) {
        Log "Device already using WinUSB. Nothing to do."
        exit 0
    }
    
    # Locate tools directory
    if ([string]::IsNullOrWhiteSpace($ToolsDir)) {
        $ToolsDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) 'tools'
    }
    
    # ── Method 1: Custom WinUSB .inf via pnputil ──
    $infFile = Join-Path $ToolsDir 'eaton_winusb.inf'
    if (Test-Path $infFile) {
        Log "Installing WinUSB driver via .inf: $infFile"
        
        # Stage the inf to a temp directory (pnputil needs writable location for catalog)
        $stagingDir = Join-Path $env:TEMP 'eaton_winusb_driver'
        if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
        New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
        Copy-Item $infFile -Destination $stagingDir -Force
        
        $stagedInf = Join-Path $stagingDir 'eaton_winusb.inf'
        
        # Create a self-signed certificate for driver signing
        Log "Creating self-signed certificate for driver catalog..."
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject "CN=Eaton 5E Controller Driver, O=WaltonComOnline" `
            -CertStoreLocation Cert:\LocalMachine\My `
            -NotAfter (Get-Date).AddYears(10) `
            -HashAlgorithm SHA256

        # Add cert to Trusted Publishers and Root so Windows accepts the driver
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
        $store.Open('ReadWrite')
        $store.Add($cert)
        $store.Close()

        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        $store.Open('ReadWrite')
        $store.Add($cert)
        $store.Close()
        Log "Certificate installed: $($cert.Thumbprint)"

        # Create and sign the catalog file
        # Use inf2cat if available (WDK), otherwise use makecat approach
        $catFile = Join-Path $stagingDir 'eaton_winusb.cat'
        
        # Create catalog definition file
        $cdfContent = @"
[CatalogHeader]
Name=$catFile
PublicVersion=0x0000001
EncodingType=0x00010001
CATATTR1=0x10010001:OSAttr:2:10.0

[CatalogFiles]
<HASH>eaton_winusb.inf=$stagedInf
"@
        $cdfFile = Join-Path $stagingDir 'eaton_winusb.cdf'
        Set-Content -Path $cdfFile -Value $cdfContent -Encoding ASCII
        
        # Try makecat first (available on most Windows installations)
        $makecatExe = "$env:SystemRoot\System32\makecat.exe"
        if (Test-Path $makecatExe) {
            Log "Creating catalog with makecat.exe..."
            $makecatResult = & $makecatExe $cdfFile 2>&1 | Out-String
            Log "makecat result: $makecatResult"
        }

        # Sign the catalog with our self-signed cert
        if (Test-Path $catFile) {
            # Use signtool if available, otherwise try Set-AuthenticodeSignature
            $signtoolExe = Get-ChildItem "C:\Program Files (x86)\Windows Kits\*\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
            if ($signtoolExe) {
                Log "Signing catalog with signtool..."
                & $signtoolExe sign /fd SHA256 /a /s My /sha1 $cert.Thumbprint $catFile 2>&1 | ForEach-Object { Log $_ }
            } else {
                Log "Signing catalog with Set-AuthenticodeSignature..."
                Set-AuthenticodeSignature -FilePath $catFile -Certificate $cert -HashAlgorithm SHA256 | Out-Null
            }
        }
        
        # Install the driver package via pnputil
        Log "Adding driver package via pnputil..."
        $pnpResult = pnputil /add-driver $stagedInf /install 2>&1 | Out-String
        Log "pnputil result: $pnpResult"
        
        # Wait for driver to settle
        Log "Waiting 5 seconds for driver to settle..."
        Start-Sleep -Seconds 5
        
        # Clean up temp certificate from Personal store (leave in TrustedPublisher for driver to work)
        Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        
        $postStatus = Get-UpsDeviceStatus
        if ($postStatus.IsWinUSB) {
            Log "SUCCESS: WinUSB driver installed via .inf"
            exit 0
        } else {
            Log "WARN: pnputil install completed but device not showing WinUSB"
            Log "Current class: $($postStatus.ClassName), driver: $($postStatus.DriverName)"
            Log "A reboot may be required, or the user can retry with Zadig."
            exit 1
        }
    }
    
    Log "ERROR: eaton_winusb.inf not found at $infFile"
    Log "WinUSB driver cannot be installed automatically."
    Log "Please install WinUSB manually using Zadig (https://zadig.akeo.ie)."
    exit 1
}

function Invoke-Rollback {
    Log "=== WinUSB Rollback ==="
    
    $status = Get-UpsDeviceStatus
    if (-not $status.Found) {
        Log "Device not found, nothing to rollback"
        exit 0
    }
    
    if (-not $status.IsWinUSB) {
        Log "Device is not using WinUSB, nothing to rollback"
        exit 0
    }
    
    Log "Attempting to remove WinUSB driver and restore default HID driver..."
    
    # Find the OEM driver inf for our Eaton WinUSB driver
    $drvOutput = pnputil /enum-drivers 2>&1 | Out-String
    $drvBlocks = $drvOutput -split '(?=Published Name:)'
    
    foreach ($block in $drvBlocks) {
        if (($block -match 'Eaton' -or $block -match '0463') -and $block -match 'WinUSB|USBDevice') {
            if ($block -match 'Published Name:\s*(oem\d+\.inf)') {
                $oemInf = $Matches[1]
                Log "Found Eaton WinUSB OEM driver: $oemInf"
                $removeResult = pnputil /delete-driver $oemInf /uninstall 2>&1 | Out-String
                Log "Remove result: $removeResult"
            }
        }
    }
    
    # Remove self-signed certificate from Trusted Publishers
    $certs = Get-ChildItem Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Subject -match 'Eaton 5E Controller Driver' }
    foreach ($c in $certs) {
        Log "Removing certificate: $($c.Thumbprint)"
        Remove-Item "Cert:\LocalMachine\TrustedPublisher\$($c.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item "Cert:\LocalMachine\Root\$($c.Thumbprint)" -ErrorAction SilentlyContinue
    }
    
    # Trigger device re-detection
    Log "Scanning for hardware changes..."
    pnputil /scan-devices 2>&1 | Out-String | ForEach-Object { Log $_ }
    
    Start-Sleep -Seconds 3
    $postStatus = Get-UpsDeviceStatus
    Log "Post-rollback device class: $($postStatus.ClassName)"
    
    Log "Rollback complete"
    exit 0
}

switch ($Action) {
    'Check'    { Invoke-Check }
    'Install'  { Invoke-Install }
    'Rollback' { Invoke-Rollback }
}
