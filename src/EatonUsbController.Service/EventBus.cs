using EatonUsbController.Core.Models;

namespace EatonUsbController.Service;

/// <summary>
/// In-process event bus for publishing/subscribing to UPS events.
/// </summary>
public sealed class EventBus
{
    private readonly List<Func<PowerEvent, Task>> _handlers = [];
    private readonly Lock _lock = new();
    private UpsStatus _lastStatus = new();

    public UpsStatus LastStatus
    {
        get { lock (_lock) return _lastStatus; }
        set { lock (_lock) _lastStatus = value; }
    }

    public void Subscribe(Func<PowerEvent, Task> handler)
    {
        lock (_lock) _handlers.Add(handler);
    }

    public async Task PublishAsync(PowerEvent evt)
    {
        Func<PowerEvent, Task>[] snapshot;
        lock (_lock) snapshot = [.. _handlers];

        foreach (var handler in snapshot)
        {
            try { await handler(evt); }
            catch { /* individual handler failure shouldn't break others */ }
        }
    }
}
