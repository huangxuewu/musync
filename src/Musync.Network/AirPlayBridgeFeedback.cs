using System.Collections.Concurrent;
using System.Text.Json;

namespace Musync.Network;

// Each helper process owns its pending requests and diagnostics. A stopped
// process cannot complete or reject requests belonging to its replacement.
internal sealed class AirPlayBridgeFeedback
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private volatile bool _stopped;
    public string? Error(string id) => _errors.GetValueOrDefault(id)
        ?? (_stopped ? "AirPlay bridge stopped." : _errors.GetValueOrDefault("*"));
    public void ClearDeviceError(string id) => _errors.TryRemove(id, out _);
    public Task RegisterRequest(string request)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request, completion)) throw new InvalidOperationException("Duplicate AirPlay request.");
        if (_stopped && _pending.TryRemove(request, out _))
            completion.TrySetException(new IOException("AirPlay bridge stopped."));
        return completion.Task;
    }
    public void ForgetRequest(string request) => _pending.TryRemove(request, out _);
    public void ReportError(string id, string error)
    {
        if (!_stopped) _errors[id] = error;
    }
    public void ObserveReply(JsonElement root)
    {
        var error = root.TryGetProperty("error", out var value) ? value.GetString() : null;
        if (root.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.String)
        {
            // A cancelled request can still receive a reply. Its error belongs
            // to that request, rather than a receiver's newer playback session.
            if (_pending.TryRemove(request.GetString()!, out var completion))
            {
                if (error is null) completion.TrySetResult(); else completion.TrySetException(new IOException(error));
            }
        }
        else if (error is not null)
            ReportError(root.TryGetProperty("id", out var id) ? id.GetString() ?? "*" : "*", error);
    }
    public void Stop()
    {
        _stopped = true;
        foreach (var request in _pending.Keys)
            if (_pending.TryRemove(request, out var completion))
                completion.TrySetException(new IOException("AirPlay bridge stopped."));
    }
}
