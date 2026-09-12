using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace EatonUsbController.Core;

/// <summary>
/// Client for the NUT (Network UPS Tools) TCP protocol on port 3493.
/// Protocol reference: https://networkupstools.org/docs/developer-guide.chunked/ar01s09.html
/// </summary>
public sealed class NutClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<NutClient> _logger;
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _authenticated;
    private volatile bool _isConnected;

    public bool IsConnected => _isConnected;

    public NutClient(string host, int port, ILogger<NutClient> logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await DisconnectInternalAsync();
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_host, _port, ct);
            var stream = _tcp.GetStream();
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _authenticated = false;
            _isConnected = true;
            _logger.LogInformation("Connected to NUT at {Host}:{Port}", _host, _port);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        var userResp = await SendCommandAsync($"USERNAME {username}", ct);
        if (!userResp.StartsWith("OK"))
            throw new InvalidOperationException($"NUT USERNAME failed: {userResp}");

        var passResp = await SendCommandAsync($"PASSWORD {password}", ct);
        if (!passResp.StartsWith("OK"))
            throw new InvalidOperationException($"NUT PASSWORD failed: {passResp}");

        _authenticated = true;
        _logger.LogDebug("Authenticated as {Username}", username);
    }

    public async Task<Dictionary<string, string>> ListVariablesAsync(string upsName, CancellationToken ct = default)
    {
        var lines = await SendListCommandAsync($"LIST VAR {upsName}", ct);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            // Format: VAR upsname varname "value"
            if (!line.StartsWith("VAR "))
                continue;

            var parsed = NutResponseParser.ParseVarLine(line);
            if (parsed.HasValue)
                vars[parsed.Value.Name] = parsed.Value.Value;
        }

        return vars;
    }

    public async Task<string> GetVariableAsync(string upsName, string varName, CancellationToken ct = default)
    {
        var response = await SendCommandAsync($"GET VAR {upsName} {varName}", ct);
        // Format: VAR upsname varname "value"
        var parsed = NutResponseParser.ParseVarLine(response);
        return parsed?.Value ?? throw new InvalidOperationException($"Failed to parse GET VAR response: {response}");
    }

    public async Task SetVariableAsync(string upsName, string varName, string value, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var response = await SendCommandAsync($"SET VAR {upsName} {varName} {value}", ct);
        if (!response.StartsWith("OK"))
            throw new InvalidOperationException($"NUT SET VAR failed: {response}");
    }

    public async Task SendInstantCommandAsync(string upsName, string command, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var response = await SendCommandAsync($"INSTCMD {upsName} {command}", ct);
        if (!response.StartsWith("OK"))
            throw new InvalidOperationException($"NUT INSTCMD {command} failed: {response}");
        _logger.LogInformation("Executed INSTCMD {Command} on {Ups}", command, upsName);
    }

    public async Task<List<string>> ListCommandsAsync(string upsName, CancellationToken ct = default)
    {
        var lines = await SendListCommandAsync($"LIST CMD {upsName}", ct);
        var cmds = new List<string>();
        foreach (var line in lines)
        {
            // Format: CMD upsname cmdname
            if (line.StartsWith("CMD "))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 3)
                    cmds.Add(parts[2]);
            }
        }
        return cmds;
    }

    public async Task<Dictionary<string, string>> ListWritableAsync(string upsName, CancellationToken ct = default)
    {
        var lines = await SendListCommandAsync($"LIST RW {upsName}", ct);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            // Format: RW upsname varname "value"
            if (line.StartsWith("RW "))
            {
                var parsed = NutResponseParser.ParseRwLine(line);
                if (parsed.HasValue)
                    vars[parsed.Value.Name] = parsed.Value.Value;
            }
        }
        return vars;
    }

    public async Task LoginAsync(string upsName, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var response = await SendCommandAsync($"LOGIN {upsName}", ct);
        if (!response.StartsWith("OK"))
            throw new InvalidOperationException($"NUT LOGIN failed: {response}");
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            await SendCommandAsync("LOGOUT", ct);
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureConnected();
            _logger.LogTrace("NUT >> {Command}", command);
            await _writer!.WriteLineAsync(command.AsMemory(), ct);
            var response = await _reader!.ReadLineAsync(ct)
                ?? throw new IOException("NUT connection closed unexpectedly");
            _logger.LogTrace("NUT << {Response}", response);

            if (response.StartsWith("ERR "))
                throw new NutProtocolException(response[4..]);

            return response;
        }
        catch (NutProtocolException) { throw; }
        catch (Exception) when (MarkDisconnected())
        {
            throw; // never reached — MarkDisconnected returns false
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<string>> SendListCommandAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureConnected();
            _logger.LogTrace("NUT >> {Command}", command);
            await _writer!.WriteLineAsync(command.AsMemory(), ct);

            var lines = new List<string>();
            while (true)
            {
                var line = await _reader!.ReadLineAsync(ct)
                    ?? throw new IOException("NUT connection closed unexpectedly");
                _logger.LogTrace("NUT << {Line}", line);

                if (line.StartsWith("ERR "))
                    throw new NutProtocolException(line[4..]);

                if (line.StartsWith("BEGIN LIST"))
                    continue;

                if (line.StartsWith("END LIST"))
                    break;

                lines.Add(line);
            }
            return lines;
        }
        catch (NutProtocolException) { throw; }
        catch (Exception) when (MarkDisconnected())
        {
            throw; // never reached
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool MarkDisconnected()
    {
        _isConnected = false;
        return false; // always false — used in exception filter
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to NUT server");
    }

    private void EnsureAuthenticated()
    {
        if (!_authenticated)
            throw new InvalidOperationException("Not authenticated — call AuthenticateAsync first");
    }

    private async Task DisconnectInternalAsync()
    {
        _isConnected = false;
        _authenticated = false;
        if (_writer is not null)
        {
            try { await _writer.DisposeAsync(); } catch { /* ignore */ }
            _writer = null;
        }
        if (_reader is not null)
        {
            try { _reader.Dispose(); } catch { /* ignore */ }
            _reader = null;
        }
        if (_tcp is not null)
        {
            try { _tcp.Dispose(); } catch { /* ignore */ }
            _tcp = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await LogoutAsync(); } catch { /* ignore */ }
        await DisconnectInternalAsync();
        _lock.Dispose();
    }
}

public class NutProtocolException(string error) : Exception($"NUT protocol error: {error}")
{
    public string NutError { get; } = error;
}
