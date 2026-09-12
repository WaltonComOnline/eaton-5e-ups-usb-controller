using EatonUsbController.Core.Models;

namespace EatonUsbController.Core;

public static class NutResponseParser
{
    public readonly record struct NutVar(string UpsName, string Name, string Value);

    /// <summary>
    /// Parses a VAR line: VAR upsname varname "value"
    /// </summary>
    public static NutVar? ParseVarLine(string line)
    {
        // VAR eaton ups.status "OL CHRG"
        if (!line.StartsWith("VAR "))
            return null;

        var span = line.AsSpan(4);
        var spaceIdx = span.IndexOf(' ');
        if (spaceIdx < 0) return null;

        var upsName = span[..spaceIdx].ToString();
        span = span[(spaceIdx + 1)..];

        // Find the variable name (everything before the first ")
        var quoteIdx = span.IndexOf('"');
        if (quoteIdx < 1) return null;

        var varName = span[..(quoteIdx - 1)].ToString().Trim();

        // Extract quoted value
        var value = ExtractQuotedValue(span[quoteIdx..]);
        return new NutVar(upsName, varName, value);
    }

    /// <summary>
    /// Parses a RW line: RW upsname varname "value"
    /// </summary>
    public static NutVar? ParseRwLine(string line)
    {
        if (!line.StartsWith("RW "))
            return null;

        // Same format as VAR, just different prefix
        var asVar = "VAR " + line[3..];
        return ParseVarLine(asVar);
    }

    private static string ExtractQuotedValue(ReadOnlySpan<char> span)
    {
        if (span.Length < 2 || span[0] != '"') return "";

        var endQuote = span[1..].IndexOf('"');
        if (endQuote < 0) return span[1..].ToString();
        return span[1..(endQuote + 1)].ToString();
    }

    /// <summary>
    /// Maps a NUT variable dictionary into an UpsStatus record.
    /// </summary>
    public static UpsStatus MapToUpsStatus(string upsName, Dictionary<string, string> vars)
    {
        return new UpsStatus
        {
            UpsName = upsName,
            RawStatus = GetStr(vars, "ups.status"),
            State = UpsStatus.ParseState(GetStr(vars, "ups.status")),
            BeeperStatus = UpsStatus.ParseBeeperState(GetStr(vars, "ups.beeper.status")),

            BatteryCharge = GetDouble(vars, "battery.charge"),
            BatteryRuntime = GetDouble(vars, "battery.runtime"),
            BatteryChargeLow = GetDouble(vars, "battery.charge.low"),
            BatteryType = GetStr(vars, "battery.type"),

            InputVoltage = GetDouble(vars, "input.voltage"),
            InputTransferHigh = GetDouble(vars, "input.transfer.high"),
            InputTransferLow = GetDouble(vars, "input.transfer.low"),

            OutputVoltage = GetDouble(vars, "output.voltage"),
            OutputVoltageNominal = GetDouble(vars, "output.voltage.nominal"),
            OutputFrequencyNominal = GetDouble(vars, "output.frequency.nominal"),

            Load = GetDouble(vars, "ups.load"),

            Manufacturer = GetStr(vars, "device.mfr", GetStr(vars, "ups.mfr")),
            Model = GetStr(vars, "device.model", GetStr(vars, "ups.model")),
            Serial = GetStr(vars, "device.serial", GetStr(vars, "ups.serial")),
            Firmware = GetStr(vars, "ups.firmware"),
            PowerNominal = GetDouble(vars, "ups.power.nominal"),

            DelayShutdown = GetDouble(vars, "ups.delay.shutdown"),
            DelayStart = GetDouble(vars, "ups.delay.start"),
            TimerShutdown = GetDouble(vars, "ups.timer.shutdown"),
            TimerStart = GetDouble(vars, "ups.timer.start"),

            OutletDescription = GetStr(vars, "outlet.1.desc"),
            OutletStatus = GetStr(vars, "outlet.1.status"),
            OutletSwitchable = GetStr(vars, "outlet.1.switchable") == "1",

            Timestamp = DateTimeOffset.UtcNow,
            RawVariables = vars
        };
    }

    private static string GetStr(Dictionary<string, string> vars, string key, string fallback = "")
        => vars.TryGetValue(key, out var val) ? val : fallback;

    private static double GetDouble(Dictionary<string, string> vars, string key)
        => vars.TryGetValue(key, out var val) && double.TryParse(val, out var d) ? d : 0;
}
