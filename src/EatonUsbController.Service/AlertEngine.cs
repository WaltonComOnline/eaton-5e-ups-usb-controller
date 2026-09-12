using System.Net.Http.Json;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;
using MailKit.Net.Smtp;
using MimeKit;

namespace EatonUsbController.Service;

/// <summary>
/// Sends email and webhook alerts for configured event types.
/// </summary>
public sealed class AlertEngine(
    EventBus eventBus,
    IOptionsMonitor<AppConfig> options,
    ILogger<AlertEngine> logger) : BackgroundService, IDisposable
{
    private readonly Dictionary<PowerEventType, DateTimeOffset> _lastAlertTime = [];
    private readonly HttpClient _httpClient = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        eventBus.Subscribe(HandleEventAsync);
        return Task.CompletedTask;
    }

    private async Task HandleEventAsync(PowerEvent evt)
    {
        var config = options.CurrentValue.Alerts;
        if (!config.EnabledEventTypes.Contains(evt.EventType))
            return;

        // Rate limit: max 1 alert per event type per 60 seconds
        if (_lastAlertTime.TryGetValue(evt.EventType, out var lastTime) &&
            DateTimeOffset.UtcNow - lastTime < TimeSpan.FromSeconds(60))
            return;

        _lastAlertTime[evt.EventType] = DateTimeOffset.UtcNow;

        if (config.Email.Enabled)
            await SendEmailAsync(config.Email, evt);

        foreach (var webhookUrl in config.WebhookUrls)
            await SendWebhookAsync(webhookUrl, evt);
    }

    private async Task SendEmailAsync(EmailSettings email, PowerEvent evt)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(email.FromAddress));
            foreach (var to in email.ToAddresses)
                message.To.Add(MailboxAddress.Parse(to));
            message.Subject = $"[EatonUsbController] {evt.EventType} — {evt.UpsName}";
            message.Body = new TextPart("plain")
            {
                Text = $"""
                    UPS Event: {evt.EventType}
                    UPS: {evt.UpsName}
                    Time: {evt.Timestamp:yyyy-MM-dd HH:mm:ss zzz}
                    Details: {evt.Details}
                    """
            };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(email.SmtpHost, email.SmtpPort, email.UseTls);
            if (!string.IsNullOrEmpty(email.Username))
                await smtp.AuthenticateAsync(email.Username, email.Password);
            await smtp.SendAsync(message);
            await smtp.DisconnectAsync(true);

            logger.LogInformation("Email alert sent for {EventType}", evt.EventType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email alert");
        }
    }

    private async Task SendWebhookAsync(string url, PowerEvent evt)
    {
        try
        {
            var payload = new
            {
                eventType = evt.EventType.ToString(),
                upsName = evt.UpsName,
                timestamp = evt.Timestamp,
                details = evt.Details
            };
            await _httpClient.PostAsJsonAsync(url, payload);
            logger.LogInformation("Webhook alert sent to {Url} for {EventType}", url, evt.EventType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send webhook to {Url}", url);
        }
    }

    public new void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
