using Microsoft.Data.Sqlite;
using EatonUsbController.Core;
using EatonUsbController.Core.Models;
using Microsoft.Extensions.Options;

namespace EatonUsbController.Service;

/// <summary>
/// SQLite-backed event and snapshot logger with configurable retention.
/// </summary>
public sealed class EventLogger : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<EventLogger> _logger;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public EventLogger(IOptionsMonitor<AppConfig> options, ILogger<EventLogger> logger)
    {
        _options = options;
        _logger = logger;

        var dataDir = AppPaths.DataDir;
        Directory.CreateDirectory(dataDir);

        var dbPath = Path.Combine(dataDir, "eatonusbcontroller.db");
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                event_type TEXT NOT NULL,
                ups_name TEXT NOT NULL,
                details TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type);

            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                ups_name TEXT NOT NULL,
                battery_charge REAL,
                battery_runtime REAL,
                input_voltage REAL,
                output_voltage REAL,
                load_percent REAL,
                state TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_snapshots_timestamp ON snapshots(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task LogEventAsync(PowerEvent evt)
    {
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO events (timestamp, event_type, ups_name, details) VALUES (@t, @e, @u, @d)";
            cmd.Parameters.AddWithValue("@t", evt.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@e", evt.EventType.ToString());
            cmd.Parameters.AddWithValue("@u", evt.UpsName);
            cmd.Parameters.AddWithValue("@d", evt.Details);
            await cmd.ExecuteNonQueryAsync();

            await PurgeOldDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log event");
        }
    }

    public async Task LogSnapshotAsync(UpsStatus status)
    {
        try
        {
            await using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO snapshots (timestamp, ups_name, battery_charge, battery_runtime, 
                    input_voltage, output_voltage, load_percent, state)
                VALUES (@t, @u, @bc, @br, @iv, @ov, @lp, @s)
                """;
            cmd.Parameters.AddWithValue("@t", status.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@u", status.UpsName);
            cmd.Parameters.AddWithValue("@bc", status.BatteryCharge);
            cmd.Parameters.AddWithValue("@br", status.BatteryRuntime);
            cmd.Parameters.AddWithValue("@iv", status.InputVoltage);
            cmd.Parameters.AddWithValue("@ov", status.OutputVoltage);
            cmd.Parameters.AddWithValue("@lp", status.Load);
            cmd.Parameters.AddWithValue("@s", status.State.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log snapshot");
        }
    }

    public async Task<List<PowerEvent>> GetEventsAsync(int maxEvents = 100, DateTimeOffset? since = null)
    {
        var events = new List<PowerEvent>();
        await using var cmd = _db.CreateCommand();

        if (since.HasValue)
        {
            cmd.CommandText =
                "SELECT timestamp, event_type, ups_name, details FROM events WHERE timestamp >= @since ORDER BY timestamp DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
        }
        else
        {
            cmd.CommandText =
                "SELECT timestamp, event_type, ups_name, details FROM events ORDER BY timestamp DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", maxEvents);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new PowerEvent(
                DateTimeOffset.Parse(reader.GetString(0)),
                Enum.Parse<PowerEventType>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return events;
    }

    public async Task<List<UpsSnapshot>> GetSnapshotsAsync(DateTimeOffset since, string upsName = "eaton")
    {
        var snapshots = new List<UpsSnapshot>();
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, battery_charge, battery_runtime, input_voltage, output_voltage, load_percent
            FROM snapshots WHERE ups_name = @u AND timestamp >= @since ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("@u", upsName);
        cmd.Parameters.AddWithValue("@since", since.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            snapshots.Add(new UpsSnapshot(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetDouble(1), reader.GetDouble(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)));
        }
        return snapshots;
    }

    private async Task PurgeOldDataAsync()
    {
        if (DateTimeOffset.UtcNow - _lastPurge < TimeSpan.FromHours(1))
            return;

        _lastPurge = DateTimeOffset.UtcNow;
        var retentionDays = _options.CurrentValue.EventRetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");

        await using var cmd1 = _db.CreateCommand();
        cmd1.CommandText = "DELETE FROM events WHERE timestamp < @cutoff";
        cmd1.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted1 = await cmd1.ExecuteNonQueryAsync();

        await using var cmd2 = _db.CreateCommand();
        cmd2.CommandText = "DELETE FROM snapshots WHERE timestamp < @cutoff";
        cmd2.Parameters.AddWithValue("@cutoff", cutoff);
        var deleted2 = await cmd2.ExecuteNonQueryAsync();

        if (deleted1 > 0 || deleted2 > 0)
            _logger.LogInformation("Purged {Events} events and {Snapshots} snapshots older than {Days} days",
                deleted1, deleted2, retentionDays);
    }

    public void Dispose() => _db.Dispose();
}

public record UpsSnapshot(
    DateTimeOffset Timestamp,
    double BatteryCharge,
    double BatteryRuntime,
    double InputVoltage,
    double OutputVoltage,
    double LoadPercent);
