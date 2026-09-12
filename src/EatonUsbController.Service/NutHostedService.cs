using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Connects to the local NUT server managed by NutHealthMonitor.
/// This service handles monitoring, alerts, beeper control, etc.
/// </summary>
public sealed class NutHostedService(
    NutClient nutClient,
    EventBus eventBus,
    IOptionsMonitor<AppConfig> options,
    ILogger<NutHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = options.CurrentValue.Nut;
            try
            {
                logger.LogInformation("Connecting to NUT server at {Host}:{Port}...",
                    config.Host, config.Port);
                await ConnectWithRetryAsync(stoppingToken);

                await eventBus.PublishAsync(new PowerEvent(
                    DateTimeOffset.UtcNow, PowerEventType.NutProcessStarted,
                    config.UpsName, "Connected to NUT server"));

                // Stay connected — reconnect if connection drops
                while (!stoppingToken.IsCancellationRequested && nutClient.IsConnected)
                {
                    await Task.Delay(5000, stoppingToken);
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("NUT connection lost — reconnecting in 5s");
                    await eventBus.PublishAsync(new PowerEvent(
                        DateTimeOffset.UtcNow, PowerEventType.NutProcessError,
                        config.UpsName, "NUT connection lost"));
                    await Task.Delay(5000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NUT connection error — retrying in 10s");
                await eventBus.PublishAsync(new PowerEvent(
                    DateTimeOffset.UtcNow, PowerEventType.NutProcessError,
                    config.UpsName, ex.Message));
                try { await Task.Delay(10000, stoppingToken); } catch { break; }
            }
        }

        await eventBus.PublishAsync(new PowerEvent(
            DateTimeOffset.UtcNow, PowerEventType.NutProcessStopped,
            options.CurrentValue.Nut.UpsName, "EatonUsbController service stopping"));
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var config = options.CurrentValue.Nut;
            if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
            {
                logger.LogWarning("NUT credentials are not configured; waiting for setup in the desktop app");
                await Task.Delay(5000, ct);
                continue;
            }

            try
            {
                await nutClient.ConnectAsync(ct);
                await nutClient.AuthenticateAsync(config.Username, config.Password, ct);
                await nutClient.LoginAsync(config.UpsName, ct);
                logger.LogInformation("Connected to NUT and logged in as {User}", config.Username);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                logger.LogDebug(ex, "NUT connect attempt {Attempt} failed", attempt);
                await Task.Delay(Math.Min(1000 * attempt, 5000), ct);
            }
        }
        ct.ThrowIfCancellationRequested();
    }
}
