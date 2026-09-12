using EatonUsbController.Core;
using EatonUsbController.Core.Models;

namespace EatonUsbController.Tests;

public sealed class NutResponseParserTests
{
    [Fact]
    public void ParseVarLine_ParsesStatusWithSpaces()
    {
        var variable = NutResponseParser.ParseVarLine("VAR eaton ups.status \"OL CHRG\"");

        Assert.NotNull(variable);
        Assert.Equal("eaton", variable.Value.UpsName);
        Assert.Equal("ups.status", variable.Value.Name);
        Assert.Equal("OL CHRG", variable.Value.Value);
    }

    [Fact]
    public void ParseRwLine_RejectsMalformedInput()
    {
        Assert.Null(NutResponseParser.ParseRwLine("RW eaton input.transfer.low"));
    }

    [Fact]
    public void MapToUpsStatus_MapsDeviceAndPowerValues()
    {
        var status = NutResponseParser.MapToUpsStatus("eaton", new Dictionary<string, string>
        {
            ["ups.status"] = "OB LB",
            ["battery.charge"] = "18.5",
            ["input.voltage"] = "229.4",
            ["device.model"] = "5E 2000i"
        });

        Assert.Equal(UpsState.LowBattery, status.State);
        Assert.Equal(18.5, status.BatteryCharge);
        Assert.Equal(229.4, status.InputVoltage);
        Assert.Equal("5E 2000i", status.Model);
    }
}