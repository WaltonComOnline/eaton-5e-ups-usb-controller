namespace EatonUsbController.Core.Models;

public enum UpsState
{
    Online,       // OL
    OnBattery,    // OB
    LowBattery,   // LB
    Charging,     // CHRG
    Discharging,  // DISCHRG
    ForcedShutdown, // FSD
    Unknown
}

public enum BeeperState
{
    Enabled,
    Disabled,
    Muted,
    Unknown
}

public record UpsStatus
{
    public string UpsName { get; init; } = "eaton";
    public UpsState State { get; init; } = UpsState.Unknown;
    public string RawStatus { get; init; } = "";
    public BeeperState BeeperStatus { get; init; } = BeeperState.Unknown;

    // Battery
    public double BatteryCharge { get; init; }
    public double BatteryRuntime { get; init; } // seconds
    public double BatteryChargeLow { get; init; }
    public string BatteryType { get; init; } = "";

    // Input
    public double InputVoltage { get; init; }
    public double InputTransferHigh { get; init; }
    public double InputTransferLow { get; init; }

    // Output
    public double OutputVoltage { get; init; }
    public double OutputVoltageNominal { get; init; }
    public double OutputFrequencyNominal { get; init; }

    // Load
    public double Load { get; init; } // percentage

    // Device info
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Firmware { get; init; } = "";
    public double PowerNominal { get; init; } // VA

    // Timers
    public double DelayShutdown { get; init; }
    public double DelayStart { get; init; }
    public double TimerShutdown { get; init; }
    public double TimerStart { get; init; }

    // Outlet
    public string OutletDescription { get; init; } = "";
    public string OutletStatus { get; init; } = "";
    public bool OutletSwitchable { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // Raw NUT variables for anything we didn't explicitly map
    public IReadOnlyDictionary<string, string> RawVariables { get; init; } =
        new Dictionary<string, string>();

    public static UpsState ParseState(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UpsState.Unknown;
        if (raw.Contains("FSD")) return UpsState.ForcedShutdown;
        if (raw.Contains("LB")) return UpsState.LowBattery;
        if (raw.Contains("OB")) return UpsState.OnBattery;
        if (raw.Contains("OL")) return UpsState.Online;
        if (raw.Contains("DISCHRG")) return UpsState.Discharging;
        if (raw.Contains("CHRG")) return UpsState.Charging;
        return UpsState.Unknown;
    }

    public static BeeperState ParseBeeperState(string raw) => raw switch
    {
        "enabled" => BeeperState.Enabled,
        "disabled" => BeeperState.Disabled,
        "muted" => BeeperState.Muted,
        _ => BeeperState.Unknown
    };
}
