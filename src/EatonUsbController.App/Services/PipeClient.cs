using System.IO;
using System.IO.Pipes;
using EatonUsbController.Core;
using EatonUsbController.Core.Contracts;
using EatonUsbController.Core.Models;

namespace EatonUsbController.App.Services;

/// <summary>
/// Named pipe client that communicates with the EatonUsbController Windows Service.
/// Receives status pushes and sends commands.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private TaskCompletionSource<PipeMessage>? _pendingResponse;

    public bool IsConnected => _pipe?.IsConnected == true;

    public event Action<UpsStatus>? OnStatusUpdate;
    public event Action<PowerEvent>? OnEvent;
    public event Action<bool>? OnConnectionChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Clean up previous connection
        if (_readCts is not null)
        {
            await _readCts.CancelAsync();
            _readCts.Dispose();
            _readCts = null;
        }
        if (_readTask is not null)
        {
            try { await _readTask; } catch { /* ignore */ }
            _readTask = null;
        }
        _reader?.Dispose();
        _reader = null;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }

        _pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await _pipe.ConnectAsync(5000, ct);
        _reader = new StreamReader(_pipe);

        // Subscribe for push updates
        await PipeProtocol.SendAsync(_pipe, new SubscribeRequest(), ct);

        OnConnectionChanged?.Invoke(true);

        // Start background read loop
        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ReadLoopAsync(_readCts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe?.IsConnected == true)
            {
                var message = await PipeProtocol.ReceiveAsync(_reader!, ct);
                if (message is null) break;

                // Server-initiated pushes must always go to their handlers — never to a
                // pending request (otherwise a broadcast can satisfy the wrong request).
                switch (message)
                {
                    case StatusUpdatePush push:
                        OnStatusUpdate?.Invoke(push.Status);
                        continue;
                    case EventPush push:
                        OnEvent?.Invoke(push.Event);
                        continue;
                }

                // Otherwise it's a response to a pending request-response exchange.
                var pending = _pendingResponse;
                if (pending is not null)
                {
                    _pendingResponse = null;
                    pending.TrySetResult(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* pipe disconnected */ }
        finally
        {
            _pendingResponse?.TrySetCanceled();
            OnConnectionChanged?.Invoke(false);
        }
    }

    public async Task<TResponse> SendRequestAsync<TResponse>(PipeMessage request, CancellationToken ct = default)
        where TResponse : PipeMessage
    {
        if (_pipe is null || !_pipe.IsConnected)
            throw new InvalidOperationException("Not connected to service");

        // Serialize request/response: the pipe uses a single shared pending-response slot,
        // so only one request-response exchange may be in flight at a time.
        await _requestLock.WaitAsync(ct);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeoutCts.Token;

            var tcs = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = tcs;

            await _writeLock.WaitAsync(token);
            try
            {
                await PipeProtocol.SendAsync(_pipe, request, token);
            }
            finally
            {
                _writeLock.Release();
            }

            using var ctr = token.Register(() => tcs.TrySetCanceled(token));
            var response = await tcs.Task;
            return response as TResponse ??
                throw new InvalidOperationException($"Unexpected response type: {response?.GetType().Name}");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        var response = await SendRequestAsync<CommandResponse>(
            new SendCommandRequest { Command = command }, ct);
        if (!response.Success)
            throw new InvalidOperationException(response.Message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_readCts is not null)
        {
            await _readCts.CancelAsync();
            _readCts.Dispose();
        }
        if (_readTask is not null)
            try { await _readTask; } catch { /* ignore */ }
        _reader?.Dispose();
        if (_pipe is not null)
            await _pipe.DisposeAsync();
    }
}
