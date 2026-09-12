using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Polls NUT via LIST VAR at a configurable interval, detects state transitions,
/// and publishes events to the EventBus.
/// </summary>
public sealed class UpsMonitor(
    NutClient nutClient,
    EventBus eventBus,
    EventLogger eventLogger,
    IOptionsMonitor<AppConfig> options,
    ILogger<UpsMonitor> logger) : BackgroundService
{
    private UpsState _lastState = UpsState.Unknown;
    private BeeperState _lastBeeperState = BeeperState.Unknown;
    private bool _wasConnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for NUT to be ready
        await Task.Delay(3000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = options.CurrentValue;
            try
            {
                if (!nutClient.IsConnected)
                {
                    if (_wasConnected)
                    {
                        _wasConnected = false;
                        await PublishEvent(PowerEventType.UpsDisconnected, config.Nut.UpsName,
                            "Lost connection to NUT");
                    }
                    await Task.Delay(config.PollIntervalMs, stoppingToken);
                    continue;
                }

                var vars = await nutClient.ListVariablesAsync(config.Nut.UpsName, stoppingToken);
                var status = NutResponseParser.MapToUpsStatus(config.Nut.UpsName, vars);

                if (!_wasConnected)
                {
                    _wasConnected = true;
                    await PublishEvent(PowerEventType.UpsConnected, config.Nut.UpsName,
                        $"Connected — {status.Manufacturer} {status.Model}");
                }

                // Detect state transitions
                await DetectTransitions(status, config.Nut.UpsName);

                // Update shared state
                eventBus.LastStatus = status;

                // Log data snapshot for charts
                await eventLogger.LogSnapshotAsync(status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NutProtocolException ex)
            {
                logger.LogWarning("NUT protocol error during poll: {Error}", ex.NutError);

                // Protocol errors (e.g. ERR UNKNOWN-UPS) mean upsd is running but the
                // UPS device is gone — typically USB unplugged and usbhid-ups crashed.
                // Treat this as a disconnect so the app shows accurate status.
                if (_wasConnected)
                {
                    _wasConnected = false;
                    _lastState = UpsState.Unknown;
                    _lastBeeperState = BeeperState.Unknown;
                    eventBus.LastStatus = new UpsStatus
                    {
                        UpsName = config.Nut.UpsName,
                        State = UpsState.Unknown,
                        RawStatus = $"ERR {ex.NutError}",
                        BeeperStatus = BeeperState.Unknown
                    };
                    await PublishEvent(PowerEventType.UpsDisconnected, config.Nut.UpsName,
                        $"NUT protocol error: {ex.NutError}");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling UPS status");
            }

            await Task.Delay(config.PollIntervalMs, stoppingToken);
        }
    }

    private async Task DetectTransitions(UpsStatus current, string upsName)
    {
        // Power state changes
        if (_lastState != current.State && _lastState != UpsState.Unknown)
        {
            var eventType = current.State switch
            {
                UpsState.OnBattery => PowerEventType.PowerLost,
                UpsState.LowBattery => PowerEventType.BatteryLow,
                UpsState.Online or UpsState.Charging when
                    _lastState is UpsState.OnBattery or UpsState.LowBattery
                    => PowerEventType.PowerRestored,
                UpsState.ForcedShutdown => PowerEventType.ForcedShutdown,
                _ => PowerEventType.StatusChanged
            };

            await PublishEvent(eventType, upsName,
                $"State changed: {_lastState} → {current.State} (raw: {current.RawStatus})");
        }
        _lastState = current.State;

        // Beeper state changes
        if (_lastBeeperState != current.BeeperStatus && _lastBeeperState != BeeperState.Unknown)
        {
            await PublishEvent(PowerEventType.BeeperChanged, upsName,
                $"Beeper: {_lastBeeperState} → {current.BeeperStatus}");
        }
        _lastBeeperState = current.BeeperStatus;
    }

    private async Task PublishEvent(PowerEventType type, string upsName, string details)
    {
        var evt = new PowerEvent(DateTimeOffset.UtcNow, type, upsName, details);
        logger.LogInformation("[Event] {Type}: {Details}", type, details);
        await eventBus.PublishAsync(evt);
        await eventLogger.LogEventAsync(evt);
    }
}
