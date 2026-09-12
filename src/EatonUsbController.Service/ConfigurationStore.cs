using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EatonUsbController.Core.Models;

namespace EatonUsbController.Service;

public sealed partial class ConfigurationStore(
    string appSettingsPath,
    string nutUsersPath,
    ILogger<ConfigurationStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Save(AppConfig config)
    {
        ValidateCredentials(config.Nut);

        var root = File.Exists(appSettingsPath)
            ? JsonNode.Parse(File.ReadAllText(appSettingsPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        root["EatonUsbController"] = JsonSerializer.SerializeToNode(config, JsonOptions);

        WriteAtomically(nutUsersPath, BuildNutUsers(config.Nut));
        WriteAtomically(appSettingsPath, root.ToJsonString(JsonOptions));
        logger.LogInformation("Application and NUT credentials configuration updated");
    }

    private static void ValidateCredentials(NutConfig config)
    {
        if (!UsernamePattern().IsMatch(config.Username))
            throw new InvalidOperationException(
                "NUT username must be 1-64 characters using letters, numbers, dot, underscore, or hyphen.");

        if (config.Password.Length is < 8 or > 128 || config.Password.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                "NUT password must be 8-128 characters and cannot contain whitespace.");
    }

    private static string BuildNutUsers(NutConfig config) =>
        $"# Managed by Eaton 5E Controller. Update credentials in NUT / Advanced.\n" +
        $"[{config.Username}]\n" +
        $"    password = {config.Password}\n" +
        "    actions = SET\n" +
        "    instcmds = ALL\n";

    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Invalid configuration path: {path}");
        Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}