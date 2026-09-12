using System.Diagnostics;
using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Monitors NUT processes (usbhid-ups, upsd) and automatically restarts them
/// if they crash. Also supports on-demand restart via the RestartNut flag.
/// </summary>
public sealed class NutHealthMonitor(
    EventBus eventBus,
    IOptionsMonitor<AppConfig> options,
    ILogger<NutHealthMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DriverStartupWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CooldownAfterRestart = TimeSpan.FromSeconds(60);

    private DateTimeOffset _lastRestartAttempt = DateTimeOffset.MinValue;
    private int _consecutiveDriverFailures;

    /// <summary>
    /// Set to true by PipeServer when the app requests a NUT restart.
    /// </summary>
    public volatile bool RestartRequested;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for initial NUT startup to complete
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (RestartRequested)
                {
                    RestartRequested = false;
                    logger.LogInformation("NUT restart requested by user");
                    await RestartNutProcessesAsync(stoppingToken);
                    _lastRestartAttempt = DateTimeOffset.UtcNow;
                    _consecutiveDriverFailures = 0;
                }
                else
                {
                    await CheckAndRestartIfNeeded(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in NUT health monitor");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAndRestartIfNeeded(CancellationToken ct)
    {
        var driverRunning = IsProcessRunning("usbhid-ups");
        var upsdRunning = IsProcessRunning("upsd");

        if (driverRunning && upsdRunning)
        {
            _consecutiveDriverFailures = 0;
            return;
        }

        // Cooldown: don't restart more than once per minute
        if (DateTimeOffset.UtcNow - _lastRestartAttempt < CooldownAfterRestart)
            return;

        _consecutiveDriverFailures++;

        if (!driverRunning)
            logger.LogWarning("usbhid-ups driver not running (failure #{Count})", _consecutiveDriverFailures);
        if (!upsdRunning)
            logger.LogWarning("upsd not running (failure #{Count})", _consecutiveDriverFailures);

        await eventBus.PublishAsync(new PowerEvent(
            DateTimeOffset.UtcNow, PowerEventType.NutProcessError,
            options.CurrentValue.Nut.UpsName,
            $"NUT process died — driver={driverRunning}, upsd={upsdRunning}. Auto-restarting..."));

        await RestartNutProcessesAsync(ct);
        _lastRestartAttempt = DateTimeOffset.UtcNow;
    }

    private async Task RestartNutProcessesAsync(CancellationToken ct)
    {
        logger.LogInformation("Restarting NUT processes...");

        // Kill any stale processes
        KillByName("usbhid-ups");
        KillByName("upsd");
        await Task.Delay(2000, ct);

        var confPath = AppPaths.NutEtc;
        var sbinPath = AppPaths.NutSbin;

        // Start driver
        var driverStarted = StartNutProcess(
            Path.Combine(sbinPath, "usbhid-ups.exe"),
            "-a eaton -D -F",
            confPath);

        if (!driverStarted)
        {
            logger.LogError("Failed to start usbhid-ups driver");
            return;
        }

        // Wait for USB enumeration
        await Task.Delay(DriverStartupWait, ct);

        if (!IsProcessRunning("usbhid-ups"))
        {
            logger.LogError("usbhid-ups driver exited immediately after start");
            return;
        }

        // Start upsd
        var upsdStarted = StartNutProcess(
            Path.Combine(sbinPath, "upsd.exe"),
            "-D",
            confPath);

        if (!upsdStarted)
        {
            logger.LogError("Failed to start upsd");
            return;
        }

        await Task.Delay(2000, ct);

        var driverOk = IsProcessRunning("usbhid-ups");
        var upsdOk = IsProcessRunning("upsd");

        if (driverOk && upsdOk)
        {
            logger.LogInformation("NUT processes restarted successfully");
            await eventBus.PublishAsync(new PowerEvent(
                DateTimeOffset.UtcNow, PowerEventType.NutProcessStarted,
                options.CurrentValue.Nut.UpsName,
                "NUT processes auto-restarted successfully"));
        }
        else
        {
            logger.LogError("NUT restart incomplete — driver={Driver}, upsd={Upsd}", driverOk, upsdOk);
        }
    }

    private bool StartNutProcess(string exe, string args, string confPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["NUT_CONFPATH"] = confPath;
            var proc = Process.Start(psi);

            return proc is not null && !proc.HasExited;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start {Exe}", exe);
            return false;
        }
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void KillByName(string name)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                proc.Kill(entireProcessTree: true);
                logger.LogDebug("Killed {Name} PID={Pid}", name, proc.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error killing {Name}", name);
        }
    }
}
