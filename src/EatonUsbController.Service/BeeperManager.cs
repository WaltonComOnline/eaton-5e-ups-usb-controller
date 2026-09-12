using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Manages beeper state based on configured mode and schedule.
/// Sends INSTCMD beeper.enable/disable/toggle to NUT as needed.
/// </summary>
public sealed class BeeperManager(
    NutClient nutClient,
    EventBus eventBus,
    IOptionsMonitor<AppConfig> options,
    ILogger<BeeperManager> logger) : BackgroundService
{
    private volatile bool _forceEnforceNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to connection events — when NUT reconnects after USB replug,
        // the UPS firmware resets beeper to enabled. We need to re-enforce immediately.
        eventBus.Subscribe(evt =>
        {
            if (evt.EventType is PowerEventType.UpsConnected or PowerEventType.BeeperChanged)
            {
                logger.LogInformation("Beeper re-enforcement triggered by {Event}", evt.EventType);
                _forceEnforceNow = true;
            }
            return Task.CompletedTask;
        });

        // Wait for NUT connection
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!nutClient.IsConnected)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                var config = options.CurrentValue.Beeper;
                var upsName = options.CurrentValue.Nut.UpsName;
                var currentStatus = eventBus.LastStatus;
                var desiredState = EvaluateDesiredState(config, currentStatus);

                if (desiredState == BeeperState.Disabled &&
                    currentStatus.BeeperStatus != BeeperState.Disabled)
                {
                    // Use beeper.off (EEPROM-persistent on Eaton HID) + beeper.disable as fallback.
                    // Re-send every cycle until the UPS confirms disabled, so it survives
                    // power cycles, firmware upgrades, and UPS resets.
                    logger.LogInformation("Beeper is {Current}, forcing off (EEPROM + session)", currentStatus.BeeperStatus);
                    await nutClient.SendInstantCommandAsync(upsName, "beeper.off", stoppingToken);
                    await nutClient.SendInstantCommandAsync(upsName, "beeper.disable", stoppingToken);
                }
                else if (desiredState == BeeperState.Enabled &&
                         currentStatus.BeeperStatus != BeeperState.Enabled)
                {
                    logger.LogInformation("Enabling beeper (EEPROM + session)");
                    await nutClient.SendInstantCommandAsync(upsName, "beeper.on", stoppingToken);
                    await nutClient.SendInstantCommandAsync(upsName, "beeper.enable", stoppingToken);
                }

                _forceEnforceNow = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in beeper manager");
            }

            // Wait 10s normally, but only 1s if a reconnection triggered immediate enforcement
            var delay = _forceEnforceNow ? 1000 : 10000;
            await Task.Delay(delay, stoppingToken);
        }
    }

    private static BeeperState EvaluateDesiredState(BeeperConfig config, UpsStatus currentStatus)
    {
        return config.Mode switch
        {
            BeeperMode.AlwaysOn => BeeperState.Enabled,
            BeeperMode.AlwaysOff => BeeperState.Disabled,
            BeeperMode.MuteOnBatteryOnly =>
                currentStatus.State is UpsState.OnBattery or UpsState.LowBattery
                    ? BeeperState.Disabled
                    : BeeperState.Enabled,
            BeeperMode.Scheduled => EvaluateSchedule(config.Schedule),
            _ => BeeperState.Unknown
        };
    }

    private static BeeperState EvaluateSchedule(List<BeeperScheduleEntry> schedule)
    {
        var now = DateTimeOffset.Now;
        var currentDay = now.DayOfWeek;
        var currentTime = TimeOnly.FromDateTime(now.DateTime);

        foreach (var entry in schedule)
        {
            if (entry.Day == currentDay &&
                currentTime >= entry.Start &&
                currentTime <= entry.End)
            {
                return entry.Muted ? BeeperState.Disabled : BeeperState.Enabled;
            }
        }

        return BeeperState.Enabled;
    }
}
