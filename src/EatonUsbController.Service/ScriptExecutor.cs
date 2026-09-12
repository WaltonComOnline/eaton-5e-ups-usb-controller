using System.Diagnostics;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Executes user-configured scripts (.bat, .ps1, .exe) on specific UPS events.
/// </summary>
public sealed class ScriptExecutor(
    EventBus eventBus,
    IOptionsMonitor<AppConfig> options,
    ILogger<ScriptExecutor> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        eventBus.Subscribe(HandleEventAsync);
        return Task.CompletedTask;
    }

    private async Task HandleEventAsync(PowerEvent evt)
    {
        var triggers = options.CurrentValue.ScriptTriggers;
        if (triggers is null || triggers.Count == 0)
            return;

        foreach (var trigger in triggers.Where(t => t.EventType == evt.EventType))
        {
            await RunScriptAsync(trigger, evt);
        }
    }

    private async Task RunScriptAsync(ScriptTrigger trigger, PowerEvent evt)
    {
        if (!File.Exists(trigger.ScriptPath))
        {
            logger.LogWarning("Script not found: {ScriptPath}", trigger.ScriptPath);
            return;
        }

        try
        {
            var ext = Path.GetExtension(trigger.ScriptPath).ToLowerInvariant();
            var (fileName, arguments) = ext switch
            {
                ".ps1" => ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{trigger.ScriptPath}\" {trigger.Arguments}"),
                ".bat" or ".cmd" => ("cmd.exe", $"/c \"{trigger.ScriptPath}\" {trigger.Arguments}"),
                _ => (trigger.ScriptPath, trigger.Arguments)
            };

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Pass event details as environment variables
            psi.Environment["EATONUSBCONTROLLER_EVENT"] = evt.EventType.ToString();
            psi.Environment["EATONUSBCONTROLLER_UPS"] = evt.UpsName;
            psi.Environment["EATONUSBCONTROLLER_DETAILS"] = evt.Details;
            psi.Environment["EATONUSBCONTROLLER_TIMESTAMP"] = evt.Timestamp.ToString("O");

            logger.LogInformation("Running script {Script} for {EventType}", trigger.ScriptPath, evt.EventType);

            using var process = Process.Start(psi);
            if (process is null)
            {
                logger.LogWarning("Failed to start script {Script}", trigger.ScriptPath);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(trigger.TimeoutSeconds));
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                    logger.LogWarning("Script {Script} exited with code {Code}: {Stderr}",
                        trigger.ScriptPath, process.ExitCode, stderr);
                else
                    logger.LogDebug("Script {Script} completed: {Stdout}", trigger.ScriptPath, stdout);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Script {Script} timed out after {Timeout}s - killing",
                    trigger.ScriptPath, trigger.TimeoutSeconds);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running script {Script}", trigger.ScriptPath);
        }
    }
}
