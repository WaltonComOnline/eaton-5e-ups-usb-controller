namespace EatonUsbController.Core.Models;

public enum PowerEventType
{
    PowerLost,
    PowerRestored,
    BatteryLow,
    BatteryNormal,
    BeeperChanged,
    ShutdownInitiated,
    ShutdownCancelled,
    UpsConnected,
    UpsDisconnected,
    TestStarted,
    TestCompleted,
    OverloadDetected,
    ForcedShutdown,
    StatusChanged,
    NutProcessStarted,
    NutProcessStopped,
    NutProcessError
}

public record PowerEvent(
    DateTimeOffset Timestamp,
    PowerEventType EventType,
    string UpsName,
    string Details);
