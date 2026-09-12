using CommunityToolkit.Mvvm.ComponentModel;
using EatonUsbController.Core.Models;

namespace EatonUsbController.App.ViewModels;

public partial class ScheduleEntryViewModel : ObservableObject
{
    [ObservableProperty] private DayOfWeek _day = DayOfWeek.Monday;
    [ObservableProperty] private string _startTime = "00:00";
    [ObservableProperty] private string _endTime = "23:59";
    [ObservableProperty] private bool _isMuted = true;
}

public partial class ScriptTriggerViewModel : ObservableObject
{
    [ObservableProperty] private string _eventType = "PowerLost";
    [ObservableProperty] private string _scriptPath = "";
    [ObservableProperty] private string _arguments = "";
    [ObservableProperty] private int _timeoutSeconds = 30;
}

public partial class EventTypeToggle : ObservableObject
{
    public PowerEventType EventType { get; init; }
    public string DisplayName { get; init; } = "";
    [ObservableProperty] private bool _isEnabled;
}
