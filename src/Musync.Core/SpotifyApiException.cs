using System.Net;

namespace Musync.Core;

public sealed class SpotifyApiException(string errorKey, HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    : HttpRequestException($"Spotify request failed ({(int)statusCode}): {errorKey}.", null, statusCode)
{
    public string ErrorKey { get; } = errorKey;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
