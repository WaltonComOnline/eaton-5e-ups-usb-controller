using System.Text.Json.Serialization;
using EatonUsbController.Core.Models;

namespace EatonUsbController.Core.Contracts;

// Named pipe protocol: JSON messages, one per line, prefixed by message type

[JsonDerivedType(typeof(GetStatusRequest), "GetStatus")]
[JsonDerivedType(typeof(GetStatusResponse), "StatusResponse")]
[JsonDerivedType(typeof(GetHistoryRequest), "GetHistory")]
[JsonDerivedType(typeof(GetHistoryResponse), "HistoryResponse")]
[JsonDerivedType(typeof(GetConfigRequest), "GetConfig")]
[JsonDerivedType(typeof(GetConfigResponse), "ConfigResponse")]
[JsonDerivedType(typeof(UpdateConfigRequest), "UpdateConfig")]
[JsonDerivedType(typeof(SendCommandRequest), "SendCommand")]
[JsonDerivedType(typeof(CommandResponse), "CommandResponse")]
[JsonDerivedType(typeof(SubscribeRequest), "Subscribe")]
[JsonDerivedType(typeof(StatusUpdatePush), "StatusUpdate")]
[JsonDerivedType(typeof(EventPush), "Event")]
[JsonDerivedType(typeof(GetNutInfoRequest), "GetNutInfo")]
[JsonDerivedType(typeof(GetNutInfoResponse), "NutInfoResponse")]
[JsonDerivedType(typeof(ListNutCommandsRequest), "ListNutCommands")]
[JsonDerivedType(typeof(ListNutCommandsResponse), "NutCommandsResponse")]
[JsonDerivedType(typeof(ListWritableVarsRequest), "ListWritableVars")]
[JsonDerivedType(typeof(ListWritableVarsResponse), "WritableVarsResponse")]
[JsonDerivedType(typeof(SetNutVariableRequest), "SetNutVariable")]
[JsonDerivedType(typeof(SendNutCommandRequest), "SendNutCommand")]
[JsonDerivedType(typeof(RestartNutRequest), "RestartNut")]
public abstract class PipeMessage;

// Status
public class GetStatusRequest : PipeMessage;
public class GetStatusResponse : PipeMessage
{
    public required UpsStatus Status { get; init; }
}

// History
public class GetHistoryRequest : PipeMessage
{
    public int MaxEvents { get; init; } = 100;
    public DateTimeOffset? Since { get; init; }
}
public class GetHistoryResponse : PipeMessage
{
    public required List<PowerEvent> Events { get; init; }
}

// Config
public class GetConfigRequest : PipeMessage;
public class GetConfigResponse : PipeMessage
{
    public required AppConfig Config { get; init; }
}
public class UpdateConfigRequest : PipeMessage
{
    public required AppConfig Config { get; init; }
}

// Commands
public class SendCommandRequest : PipeMessage
{
    public required string Command { get; init; } // e.g. "beeper.enable", "test.battery.start.quick"
}
public class CommandResponse : PipeMessage
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

// Subscriptions (service pushes updates to app)
public class SubscribeRequest : PipeMessage;

public class StatusUpdatePush : PipeMessage
{
    public required UpsStatus Status { get; init; }
}
public class EventPush : PipeMessage
{
    public required PowerEvent Event { get; init; }
}

// NUT info (for advanced UI page)
public class GetNutInfoRequest : PipeMessage;
public class GetNutInfoResponse : PipeMessage
{
    public bool DriverRunning { get; init; }
    public bool UpsdRunning { get; init; }
    public bool Connected { get; init; }
}

public class ListNutCommandsRequest : PipeMessage;
public class ListNutCommandsResponse : PipeMessage
{
    public required List<string> Commands { get; init; }
}

public class ListWritableVarsRequest : PipeMessage;
public class ListWritableVarsResponse : PipeMessage
{
    public required Dictionary<string, string> Variables { get; init; }
}

public class SetNutVariableRequest : PipeMessage
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public class SendNutCommandRequest : PipeMessage
{
    public required string Command { get; init; }
}

public class RestartNutRequest : PipeMessage;
