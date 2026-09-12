using System.Text.Json.Serialization;

namespace EatonUsbController.Core.Models;

public class AppConfig
{
    public NutConfig Nut { get; set; } = new();
    public BeeperConfig Beeper { get; set; } = new();
    public ShutdownConfig Shutdown { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    public List<ScriptTrigger> ScriptTriggers { get; set; } = [];
    public int PollIntervalMs { get; set; } = 2000;
    public int EventRetentionDays { get; set; } = 90;
}

public class NutConfig
{
    public string UpsName { get; set; } = "eaton";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3493;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string NutBinPath { get; set; } = "";
    public string NutConfPath { get; set; } = "";
}

public class BeeperConfig
{
    public BeeperMode Mode { get; set; } = BeeperMode.AlwaysOff;
    public List<BeeperScheduleEntry> Schedule { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeeperMode
{
    AlwaysOn,
    AlwaysOff,
    Scheduled,
    MuteOnBatteryOnly
}

public record BeeperScheduleEntry(
    DayOfWeek Day,
    TimeOnly Start,
    TimeOnly End,
    bool Muted);

public class ShutdownConfig
{
    public bool Enabled { get; set; }
    public int BatteryThresholdPercent { get; set; } = 20;
    public int RuntimeThresholdSeconds { get; set; } = 120;
    public int GracePeriodSeconds { get; set; } = 30;
    public bool UseHibernate { get; set; }
    public string CustomShutdownCommand { get; set; } = "";
}

public class AlertConfig
{
    public EmailSettings Email { get; set; } = new();
    public List<string> WebhookUrls { get; set; } = [];
    public HashSet<PowerEventType> EnabledEventTypes { get; set; } =
    [
        PowerEventType.PowerLost,
        PowerEventType.PowerRestored,
        PowerEventType.BatteryLow,
        PowerEventType.ShutdownInitiated,
        PowerEventType.ForcedShutdown
    ];
}

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public List<string> ToAddresses { get; set; } = [];
}

public record ScriptTrigger(
    PowerEventType EventType,
    string ScriptPath,
    string Arguments = "",
    int TimeoutSeconds = 30);
