param(
    [ValidateSet('Install','Upgrade','Uninstall','Rollback')]
    [string]$Action,
    [string]$InstallDir
)

$ErrorActionPreference = 'Stop'

# Auto-detect install directory from script location if not provided
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
} else {
    $InstallDir = $InstallDir.TrimEnd('"').TrimEnd('\', '/')
}

$logFile = Join-Path $env:TEMP 'EatonUsbController-Setup.log'
$script:CompletedActions = @()
$script:SetupFailed = $false
$script:FailureReasons = @()

function Log($msg) {
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "$ts $msg" | Tee-Object -FilePath $logFile -Append | Write-Host
}

# ── Validation helpers ──

function Test-ServiceRunning {
    param([int]$MaxRetries = 3, [int]$DelaySec = 2)
    for ($i = 1; $i -le $MaxRetries; $i++) {
        $svc = Get-Service -Name EatonUsbController -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            Log "  Service is running (attempt $i)"
            return $true
        }
        if ($i -lt $MaxRetries) {
            Log "  Service not running yet, retrying in ${DelaySec}s (attempt $i/$MaxRetries)..."
            Start-Sleep -Seconds $DelaySec
        }
    }
    Log "  WARN: Service not running after $MaxRetries attempts"
    return $false
}

function Test-JunctionValid {
    $junction = Get-Item 'C:\NUT' -Force -ErrorAction SilentlyContinue
    if (-not $junction) { return $false }
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { return $false }

    $actualTarget = [IO.Path]::GetFullPath([string]$junction.Target).TrimEnd('\')
    $expectedTarget = [IO.Path]::GetFullPath((Join-Path $InstallDir 'nut')).TrimEnd('\')
    return $actualTarget.Equals($expectedTarget, [StringComparison]::OrdinalIgnoreCase) -and
        ((Test-Path 'C:\NUT\sbin\usbhid-ups.exe') -or (Test-Path 'C:\NUT\sbin\upsd.exe'))
}

function Ensure-NutJunction {
    $nutPath = Join-Path $InstallDir 'nut'
    $junction = Get-Item 'C:\NUT' -Force -ErrorAction SilentlyContinue

    if ($junction -and -not (Test-JunctionValid)) {
        if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw 'C:\NUT exists as a real directory. Move or remove it manually before installation.'
        }
        Log "Replacing stale C:\NUT junction (target: $($junction.Target))"
        cmd /c 'rmdir C:\NUT' 2>&1 | ForEach-Object { Log $_ }
        $junction = $null
    }

    if (-not $junction) {
        Log "Creating junction C:\NUT -> $nutPath"
        cmd /c "mklink /J C:\NUT `"$nutPath`"" 2>&1 | ForEach-Object { Log $_ }
        $script:CompletedActions += 'Junction'
    } else {
        Log "Junction C:\NUT already targets $nutPath"
    }

    if (-not (Test-JunctionValid)) {
        throw "Junction C:\NUT does not target $nutPath or required NUT binaries are missing"
    }
}

function Record-Failure([string]$reason) {
    $script:SetupFailed = $true
    $script:FailureReasons += $reason
    Log "FAILURE: $reason"
}

function Set-ControllerService([string]$ServiceExe) {
    if (Get-Service -Name EatonUsbController -ErrorAction SilentlyContinue) {
        Log "Updating service binPath to: $ServiceExe"
        sc.exe config EatonUsbController binPath= "`"$ServiceExe`"" 2>&1 | ForEach-Object { Log $_ }
    } else {
        Log "Creating EatonUsbController service"
        New-Service -Name EatonUsbController `
            -BinaryPathName "`"$ServiceExe`"" `
            -DisplayName 'EatonUsbController UPS Monitor' `
            -Description 'Monitors UPS status via NUT and provides alerts, beeper control, and shutdown management.' `
            -StartupType Automatic | Out-Null
        $script:CompletedActions += 'Service'
    }
}

function Restore-UpgradeState {
    $backupConfig = Join-Path $env:ProgramData 'EatonUsbController\upgrade-appsettings.json'
    $backupNutUsers = Join-Path $env:ProgramData 'EatonUsbController\upgrade-upsd.users'
    $legacyConfig = Join-Path $InstallDir 'appsettings.json'
    $serviceConfig = Join-Path $InstallDir 'service\appsettings.json'
    $nutUsers = Join-Path $InstallDir 'nut\etc\upsd.users'

    if (Test-Path $backupConfig) {
        Log "Restoring configuration to $serviceConfig"
        Copy-Item $backupConfig $serviceConfig -Force
        Remove-Item $backupConfig -Force
    } elseif (Test-Path $legacyConfig) {
        Log "Migrating configuration to $serviceConfig"
        Copy-Item $legacyConfig $serviceConfig -Force
    }

    if (Test-Path $backupNutUsers) {
        Log "Restoring NUT users to $nutUsers"
        Copy-Item $backupNutUsers $nutUsers -Force
        Remove-Item $backupNutUsers -Force
    }
}

# ── Install (fresh) ──

function Install-EatonUsbController {
    Log "=== EatonUsbController FRESH INSTALL - InstallDir=$InstallDir ==="

    try {
        # 1. Create directory junction C:\NUT -> <InstallDir>\nut
        Ensure-NutJunction
        Restore-UpgradeState

        # 2. Register Windows Service
        $svcExe = Join-Path $InstallDir 'service\EatonUsbController.Service.exe'
        Log "Service exe: $svcExe"
        Set-ControllerService $svcExe
        Log "Starting service"
        Start-Service EatonUsbController -ErrorAction SilentlyContinue

        # Validate service
        if (-not (Test-ServiceRunning)) {
            Record-Failure "Service failed to start"
        }

        # Remove legacy startup task. NutHealthMonitor is the sole NUT process owner.
        Unregister-ScheduledTask -TaskName 'EatonUsbController-NUT' -Confirm:$false -ErrorAction SilentlyContinue

        Log "Completed actions: $($script:CompletedActions -join ', ')"
    }
    catch {
        Log "EXCEPTION during install: $_"
        Record-Failure "Exception: $_"
        Log "Initiating automatic rollback..."
        Undo-CompletedActions
    }

    Write-SetupSummary 'Install'
}

# ── Upgrade (preserves config) ──

function Upgrade-EatonUsbController {
    Log "=== EatonUsbController UPGRADE - InstallDir=$InstallDir ==="

    try {
        # 1. Stop existing NUT processes so binaries can be replaced
        Log "Stopping NUT processes for upgrade..."
        Get-Process -Name usbhid-ups,upsd -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 1

        # 2. Stop service for binary update
        Log "Stopping service for upgrade..."
        Stop-Service EatonUsbController -Force -ErrorAction SilentlyContinue
        Start-Sleep 2

        # 3. Restore v6.2.0 credentials and update the service binary path.
        $svcExe = Join-Path $InstallDir 'service\EatonUsbController.Service.exe'
        Restore-UpgradeState
        Set-ControllerService $svcExe

        # 4. Ensure junction is valid
        Ensure-NutJunction

        # 5. Start service with updated binaries
        Log "Starting service..."
        Start-Service EatonUsbController -ErrorAction SilentlyContinue

        if (-not (Test-ServiceRunning)) {
            Record-Failure "Service failed to start after upgrade"
        }

        # Remove legacy startup task. NutHealthMonitor is the sole NUT process owner.
        Unregister-ScheduledTask -TaskName 'EatonUsbController-NUT' -Confirm:$false -ErrorAction SilentlyContinue
    }
    catch {
        Log "EXCEPTION during upgrade: $_"
        Record-Failure "Exception: $_"
    }

    Write-SetupSummary 'Upgrade'
}

# ── Uninstall ──

function Uninstall-EatonUsbController {
    Log "=== EatonUsbController UNINSTALL ==="

    # Stop NUT processes
    Log "Stopping NUT processes"
    Get-Process -Name usbhid-ups,upsd -ErrorAction SilentlyContinue | Stop-Process -Force

    # Stop and remove service
    Log "Stopping and removing service"
    Stop-Service EatonUsbController -Force -ErrorAction SilentlyContinue
    sc.exe delete EatonUsbController 2>&1 | ForEach-Object { Log $_ }

    # Remove the legacy scheduled task if upgrading from an older release
    Log "Removing scheduled task"
    Unregister-ScheduledTask -TaskName 'EatonUsbController-NUT' -Confirm:$false -ErrorAction SilentlyContinue

    # Remove junction (not the target)
    $junction = Get-Item 'C:\NUT' -Force -ErrorAction SilentlyContinue
    if ($junction -and ($junction.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Log "Removing junction C:\NUT"
        cmd /c "rmdir C:\NUT" 2>&1 | ForEach-Object { Log $_ }
    }
    
    Log "Uninstall complete"
}

# ── Rollback (undo partial install) ──

function Undo-CompletedActions {
    Log "Rolling back completed actions: $($script:CompletedActions -join ', ')"
    
    foreach ($action in $script:CompletedActions) {
        try {
            switch ($action) {
                'Service' {
                    Log "  Rollback: removing service"
                    Stop-Service EatonUsbController -Force -ErrorAction SilentlyContinue
                    sc.exe delete EatonUsbController 2>&1 | ForEach-Object { Log "  $_" }
                }
                'Junction' {
                    Log "  Rollback: removing junction"
                    $junction = Get-Item 'C:\NUT' -Force -ErrorAction SilentlyContinue
                    if ($junction -and ($junction.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                        cmd /c "rmdir C:\NUT" 2>&1 | ForEach-Object { Log "  $_" }
                    }
                }
            }
        }
        catch {
            Log "  Rollback warning for ${action}: $_"
        }
    }
    Log "Rollback complete"
}

function Invoke-Rollback {
    Log "=== EatonUsbController EXPLICIT ROLLBACK ==="
    # Full cleanup — same as uninstall but tolerant of missing items
    Uninstall-EatonUsbController
}

# ── Summary ──

function Write-SetupSummary([string]$mode) {
    Log "────────────────────────────────────"
    if ($script:SetupFailed) {
        Log "SETUP RESULT: $mode COMPLETED WITH ERRORS"
        foreach ($reason in $script:FailureReasons) {
            Log "  - $reason"
        }
        Log "Log file: $logFile"
        Log "────────────────────────────────────"
        exit 1
    } else {
        Log "SETUP RESULT: $mode SUCCESSFUL"
        Log "────────────────────────────────────"
        exit 0
    }
}

# ── Entry point ──

switch ($Action) {
    'Install'   { Install-EatonUsbController }
    'Upgrade'   { Upgrade-EatonUsbController }
    'Uninstall' { Uninstall-EatonUsbController }
    'Rollback'  { Invoke-Rollback }
}
