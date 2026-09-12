using System.IO.Pipes;
using System.Text.Json;
using EatonUsbController.Core.Contracts;

namespace EatonUsbController.Core;

/// <summary>
/// Serialization helpers for named pipe JSON messages.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "EatonUsbController";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task SendAsync(Stream stream, PipeMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        await writer.WriteLineAsync(json.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    public static async Task<PipeMessage?> ReceiveAsync(StreamReader reader, CancellationToken ct = default)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null) return null;
        return JsonSerializer.Deserialize<PipeMessage>(line, JsonOptions);
    }
}
