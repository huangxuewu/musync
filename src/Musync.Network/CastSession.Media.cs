using Sharpcaster.Extensions;
using Sharpcaster.Interfaces;
using Sharpcaster.Messages.Media;
using Sharpcaster.Models.Media;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Musync.Network;

internal readonly record struct CastMediaBinding(long Load, long Session, string TransportId);

internal sealed partial class CastSession
{
    private CastMediaBinding RequireMediaBinding() => _media.Binding
        ?? throw new InvalidOperationException("Chromecast music is not prepared.");

    private async Task<(CastMediaBinding Binding, MediaStatus Status)> LoadMediaAsync(Media media, bool autoPlay, CancellationToken token)
    {
        var application = _client.ChromecastStatus.Application
            ?? throw new InvalidOperationException("No running Chromecast application found.");
        var load = _media.BeginLoad(application.TransportId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var request = new LoadMessage { SessionId = application.SessionId, Media = media, AutoPlay = autoPlay };
            var status = await SendMediaRequestAsync(load, application.TransportId, null, request,
                SharpcasteSerializationContext.Default.LoadMessage, deadline.Token);
            if (status is null) throw new IOException("Chromecast did not accept the audio stream.");
            _media.AcceptLoad(load, status);
            if (_media.Error is { } error) throw new IOException(error);
            // Bind the initial response before preparation completes. Polling
            // avoids losing a completion broadcast between same-ID waiters.
            var binding = RequireMediaBinding();
            if (binding.Load != load) throw new OperationCanceledException("Chromecast playback was replaced.");
            while (status.ExtendedStatus?.PlayerState == PlayerStateType.Loading)
            {
                await Task.Delay(100, deadline.Token);
                status = await ReadOwnedStatusAsync(binding, deadline.Token)
                    ?? throw new IOException("Chromecast lost the loading music.");
            }
            return (binding, status);
        }
        catch (IOException error) { _media.Fail(load, error.Message); throw; }
    }

    private Task<MediaStatus?> ReadOwnedStatusAsync(CastMediaBinding binding, CancellationToken token = default) =>
        SendOwnedMediaAsync(binding, new Sharpcaster.Messages.Receiver.GetStatusMessage(),
            SharpcasteSerializationContext.Default.GetStatusMessage, token);

    private async Task<MediaStatus?> SendOwnedMediaAsync<T>(CastMediaBinding binding, T request, JsonTypeInfo<T> context,
        CancellationToken token = default, bool observe = true) where T : IMessageWithId
    {
        var status = await SendMediaRequestAsync(binding.Load, binding.TransportId, binding.Session, request, context, token, observe);
        if (observe && status is not null)
        {
            _media.Observe(status);
            if (_media.Error is { } error) throw new IOException(error);
        }
        return status;
    }

    private async Task<MediaStatus?> SendMediaRequestAsync<T>(long load, string transport, long? session, T request,
        JsonTypeInfo<T> context, CancellationToken token, bool observe = true) where T : IMessageWithId
    {
        if (observe) EnsureCurrentLoad(load);
        var response = await _client.SendAsync(null, _client.MediaChannel.Namespace, request.RequestId,
            JsonSerializer.Serialize(request, context), transport).WaitAsync(token);
        using var json = JsonDocument.Parse(response);
        var payload = json.RootElement;
        if (!payload.TryGetProperty("requestId", out var id) || id.GetInt32() != request.RequestId)
            throw new IOException("Chromecast returned a response to a different audio request.");
        // Request errors belong to their awaiting operation. Global channel
        // events cannot establish ownership and can describe completed requests.
        var type = payload.GetProperty("type").GetString();
        var error = type switch
        {
            "LOAD_FAILED" => "Chromecast could not load the audio stream.",
            "LOAD_CANCELLED" => "Chromecast canceled the audio load.",
            "INVALID_REQUEST" => "Chromecast rejected the audio request: " +
                (payload.TryGetProperty("reason", out var reason) ? reason.GetString() : "INVALID_REQUEST"),
            "INVALID_PLAYER_STATE" => "Chromecast cannot perform the audio request in its current state.",
            "ERROR" => "Chromecast media error (" +
                (payload.TryGetProperty("detailedErrorCode", out var code) ? code.ToString() : "unknown") + ").",
            _ => null
        };
        if (error is not null) throw new IOException(error);
        if (type != "MEDIA_STATUS") throw new IOException("Chromecast returned an unexpected audio response.");
        if (observe) EnsureCurrentLoad(load);
        var statuses = JsonSerializer.Deserialize(response, SharpcasteSerializationContext.Default.MediaStatusMessage)?.Status;
        return session is { } owned ? statuses?.FirstOrDefault(item => item.MediaSessionId == owned) : statuses?.FirstOrDefault();
    }

    private void EnsureCurrentLoad(long load)
    {
        if (!_media.IsCurrent(load)) throw new OperationCanceledException("Chromecast playback was stopped or replaced.");
    }
}
