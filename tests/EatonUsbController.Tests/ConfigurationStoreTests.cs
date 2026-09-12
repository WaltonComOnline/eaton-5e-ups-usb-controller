using System.Text.Json;
using EatonUsbController.Core.Models;
using EatonUsbController.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace EatonUsbController.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"EatonUsbController.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_PreservesOtherSettingsAndWritesNutCredentials()
    {
        Directory.CreateDirectory(_directory);
        var appSettingsPath = Path.Combine(_directory, "appsettings.json");
        var nutUsersPath = Path.Combine(_directory, "nut", "upsd.users");
        File.WriteAllText(appSettingsPath, """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "EatonUsbController": {}
            }
            """);
        var store = CreateStore(appSettingsPath, nutUsersPath);

        store.Save(new AppConfig
        {
            Nut = new NutConfig { Username = "operator", Password = "strong-password" },
            Beeper = new BeeperConfig { Mode = BeeperMode.AlwaysOff }
        });

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        Assert.Equal("Information", document.RootElement
            .GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());
        Assert.Equal("operator", document.RootElement
            .GetProperty("EatonUsbController").GetProperty("Nut").GetProperty("Username").GetString());
        Assert.Equal("AlwaysOff", document.RootElement
            .GetProperty("EatonUsbController").GetProperty("Beeper").GetProperty("Mode").GetString());

        var nutUsers = File.ReadAllText(nutUsersPath);
        Assert.Contains("[operator]", nutUsers);
        Assert.Contains("password = strong-password", nutUsers);
        Assert.DoesNotContain("upsbeeper", nutUsers);
    }

    [Theory]
    [InlineData("", "strong-password")]
    [InlineData("bad user", "strong-password")]
    [InlineData("operator", "short")]
    [InlineData("operator", "bad password")]
    public void Save_RejectsInvalidCredentials(string username, string password)
    {
        Directory.CreateDirectory(_directory);
        var store = CreateStore(
            Path.Combine(_directory, "appsettings.json"),
            Path.Combine(_directory, "upsd.users"));

        var error = Assert.Throws<InvalidOperationException>(() => store.Save(new AppConfig
        {
            Nut = new NutConfig { Username = username, Password = password }
        }));

        Assert.Contains("NUT", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ConfigurationStore CreateStore(string appSettingsPath, string nutUsersPath) =>
        new(appSettingsPath, nutUsersPath, NullLogger<ConfigurationStore>.Instance);
}