using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using EatonUsbController.Core;
using EatonUsbController.Core.Contracts;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// Named pipe server that exposes UPS data and commands to the WPF app.
/// Supports multiple concurrent clients with push updates.
/// </summary>
public sealed class PipeServer(
    NutClient nutClient,
    EventBus eventBus,
    EventLogger eventLogger,
    NutHealthMonitor nutHealthMonitor,
    ConfigurationStore configurationStore,
    IOptionsMonitor<AppConfig> options,
    ILogger<PipeServer> logger) : BackgroundService
{
    private readonly List<ConnectedClient> _clients = [];
    private readonly Lock _clientsLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to event bus for pushing updates to clients
        eventBus.Subscribe(PushEventToClients);

        // Start periodic status push
        _ = PushStatusLoop(stoppingToken);

        // Accept connections
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                var pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);

                await pipe.WaitForConnectionAsync(stoppingToken);
                logger.LogInformation("Pipe client connected");

                var client = new ConnectedClient(pipe);
                lock (_clientsLock) _clients.Add(client);

                _ = HandleClientAsync(client, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error accepting pipe connection");
            }
        }
    }

    private async Task HandleClientAsync(ConnectedClient client, CancellationToken ct)
    {
        try
        {
            var reader = new StreamReader(client.Pipe);
            while (!ct.IsCancellationRequested && client.Pipe.IsConnected)
            {
                var message = await PipeProtocol.ReceiveAsync(reader, ct);
                if (message is null) break;

                var response = await ProcessMessageAsync(message);
                if (response is not null)
                {
                    await client.WriteLock.WaitAsync(ct);
                    try
                    {
                        await PipeProtocol.SendAsync(client.Pipe, response, ct);
                    }
                    finally
                    {
                        client.WriteLock.Release();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Pipe client disconnected");
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            client.Pipe.Dispose();
        }
    }

    private async Task<PipeMessage?> ProcessMessageAsync(PipeMessage message)
    {
        return message switch
        {
            GetStatusRequest => new GetStatusResponse { Status = eventBus.LastStatus },

            GetHistoryRequest req => new GetHistoryResponse
            {
                Events = await eventLogger.GetEventsAsync(req.MaxEvents, req.Since)
            },

            GetConfigRequest => new GetConfigResponse { Config = options.CurrentValue },

            UpdateConfigRequest req => HandleUpdateConfig(req),

            SendCommandRequest req => await HandleSendCommandAsync(req),

            GetNutInfoRequest => new GetNutInfoResponse
            {
                Connected = nutClient.IsConnected,
                DriverRunning = IsProcessRunning("usbhid-ups"),
                UpsdRunning = IsProcessRunning("upsd")
            },

            ListNutCommandsRequest => new ListNutCommandsResponse
            {
                Commands = await nutClient.ListCommandsAsync(options.CurrentValue.Nut.UpsName)
            },

            ListWritableVarsRequest => new ListWritableVarsResponse
            {
                Variables = await nutClient.ListWritableAsync(options.CurrentValue.Nut.UpsName)
            },

            SetNutVariableRequest req => await HandleSetVariableAsync(req),

            SendNutCommandRequest req => await HandleNutCommandAsync(req),

            RestartNutRequest => HandleRestartNut(),

            SubscribeRequest => null, // Subscription is implicit when connected

            _ => new CommandResponse { Success = false, Message = "Unknown message type" }
        };
    }

    private PipeMessage HandleUpdateConfig(UpdateConfigRequest req)
    {
        try
        {
            configurationStore.Save(req.Config);
            nutHealthMonitor.RestartRequested = true;
            return new CommandResponse
            {
                Success = true,
                Message = "Configuration saved; NUT is restarting with the new credentials"
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Configuration update rejected");
            return new CommandResponse { Success = false, Message = ex.Message };
        }
    }

    private async Task<PipeMessage> HandleSendCommandAsync(SendCommandRequest req)
    {
        try
        {
            var upsName = options.CurrentValue.Nut.UpsName;
            await nutClient.SendInstantCommandAsync(upsName, req.Command);
            return new CommandResponse { Success = true, Message = $"Command {req.Command} sent" };
        }
        catch (Exception ex)
        {
            return new CommandResponse { Success = false, Message = ex.Message };
        }
    }

    private async Task<PipeMessage> HandleSetVariableAsync(SetNutVariableRequest req)
    {
        try
        {
            var upsName = options.CurrentValue.Nut.UpsName;
            await nutClient.SetVariableAsync(upsName, req.Name, req.Value);
            return new CommandResponse { Success = true, Message = $"Variable {req.Name} set to {req.Value}" };
        }
        catch (Exception ex)
        {
            return new CommandResponse { Success = false, Message = ex.Message };
        }
    }

    private async Task<PipeMessage> HandleNutCommandAsync(SendNutCommandRequest req)
    {
        try
        {
            var upsName = options.CurrentValue.Nut.UpsName;
            await nutClient.SendInstantCommandAsync(upsName, req.Command);
            return new CommandResponse { Success = true, Message = $"NUT command {req.Command} executed" };
        }
        catch (Exception ex)
        {
            return new CommandResponse { Success = false, Message = ex.Message };
        }
    }

    private PipeMessage HandleRestartNut()
    {
        nutHealthMonitor.RestartRequested = true;
        logger.LogInformation("NUT restart requested via pipe");
        return new CommandResponse { Success = true, Message = "NUT restart triggered — processes will restart within 30 seconds" };
    }

    private static bool IsProcessRunning(string name)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    private async Task PushStatusLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                var status = eventBus.LastStatus;
                var msg = new StatusUpdatePush { Status = status };
                await BroadcastAsync(msg, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore broadcast errors */ }
        }
    }

    private async Task PushEventToClients(PowerEvent evt)
    {
        await BroadcastAsync(new EventPush { Event = evt });
    }

    private async Task BroadcastAsync(PipeMessage message, CancellationToken ct = default)
    {
        ConnectedClient[] snapshot;
        lock (_clientsLock) snapshot = [.. _clients];

        foreach (var client in snapshot)
        {
            try
            {
                if (client.Pipe.IsConnected)
                {
                    await client.WriteLock.WaitAsync(ct);
                    try
                    {
                        await PipeProtocol.SendAsync(client.Pipe, message, ct);
                    }
                    finally
                    {
                        client.WriteLock.Release();
                    }
                }
            }
            catch
            {
                // Client disconnected — will be cleaned up in HandleClientAsync
            }
        }
    }

    private sealed class ConnectedClient(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }
}
