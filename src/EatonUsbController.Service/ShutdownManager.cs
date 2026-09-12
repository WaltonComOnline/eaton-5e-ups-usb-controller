using System.Runtime.InteropServices;
using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Monitors battery thresholds and initiates graceful system shutdown
/// when on battery and thresholds are exceeded.
/// </summary>
public sealed class ShutdownManager(
    NutClient nutClient,
    EventBus eventBus,
    EventLogger eventLogger,
    IOptionsMonitor<AppConfig> options,
    ILogger<ShutdownManager> logger) : BackgroundService
{
    private bool _countdownActive;
    private DateTimeOffset _countdownStart;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        eventBus.Subscribe(OnEvent);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = options.CurrentValue.Shutdown;
                if (!config.Enabled)
                {
                    _countdownActive = false;
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                var status = eventBus.LastStatus;

                var onBattery = status.State is UpsState.OnBattery or UpsState.LowBattery;
                var belowCharge = status.BatteryCharge > 0 && status.BatteryCharge <= config.BatteryThresholdPercent;
                var belowRuntime = status.BatteryRuntime > 0 && status.BatteryRuntime <= config.RuntimeThresholdSeconds;

                if (onBattery && (belowCharge || belowRuntime))
                {
                    if (!_countdownActive)
                    {
                        _countdownActive = true;
                        _countdownStart = DateTimeOffset.UtcNow;
                        logger.LogWarning("Shutdown countdown started — battery {Charge}%, runtime {Runtime}s",
                            status.BatteryCharge, status.BatteryRuntime);

                        var evt = new PowerEvent(DateTimeOffset.UtcNow, PowerEventType.ShutdownInitiated,
                            status.UpsName,
                            $"Battery {status.BatteryCharge}%, runtime {status.BatteryRuntime}s — " +
                            $"shutting down in {config.GracePeriodSeconds}s");
                        await eventBus.PublishAsync(evt);
                        await eventLogger.LogEventAsync(evt);
                    }

                    var elapsed = DateTimeOffset.UtcNow - _countdownStart;
                    if (elapsed.TotalSeconds >= config.GracePeriodSeconds)
                    {
                        await ExecuteShutdownAsync(config, status.UpsName, stoppingToken);
                        return; // We're shutting down
                    }
                }
                else if (_countdownActive)
                {
                    // Power restored or thresholds no longer exceeded
                    _countdownActive = false;
                    logger.LogInformation("Shutdown cancelled — condition resolved");

                    var evt = new PowerEvent(DateTimeOffset.UtcNow, PowerEventType.ShutdownCancelled,
                        status.UpsName, "Power restored or battery level improved");
                    await eventBus.PublishAsync(evt);
                    await eventLogger.LogEventAsync(evt);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in shutdown manager");
            }

            await Task.Delay(2000, stoppingToken);
        }
    }

    private Task OnEvent(PowerEvent evt)
    {
        // Cancel countdown if power restored
        if (evt.EventType == PowerEventType.PowerRestored)
            _countdownActive = false;
        return Task.CompletedTask;
    }

    private async Task ExecuteShutdownAsync(ShutdownConfig config, string upsName, CancellationToken ct)
    {
        logger.LogCritical("Executing system shutdown!");

        // Tell UPS to shut down after delay (so it can restart when power returns)
        try
        {
            await nutClient.SendInstantCommandAsync(upsName, "shutdown.return", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send shutdown.return to UPS");
        }

        if (!string.IsNullOrEmpty(config.CustomShutdownCommand))
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {config.CustomShutdownCommand}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        else if (config.UseHibernate)
        {
            System.Diagnostics.Process.Start("shutdown", "/h /t 0");
        }
        else
        {
            System.Diagnostics.Process.Start("shutdown", "/s /t 0 /c \"UPS battery critical — EatonUsbController auto-shutdown\"");
        }
    }
}
