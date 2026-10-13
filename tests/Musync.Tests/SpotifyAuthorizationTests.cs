using Musync.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Musync.Tests;

public sealed class SpotifyAuthorizationTests
{
    private const string ClientId = "0123456789abcdef0123456789abcdef";
    private const string ValidTokens = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
    private sealed class Vault : ITokenVault
    {
        public string? Json;
        public int Saves;
        public string? Load() => Json;
        public void Save(string json) { Json = json; Saves++; }
        public void Clear() => Json = null;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return respond(request, token); }
    }
    private static Dictionary<string, string> Query(string input) => input.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
    private static SpotifyClient Create(Vault vault, Handler handler, TimeSpan? timeout = null)
        => new(vault, new HttpClient(handler), callbackPort: 0, callbackRequestTimeout: TimeSpan.FromMilliseconds(150),
            authorizationTimeout: timeout) { ClientId = ClientId };
    private static Handler SuccessfulHandler() => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) }));
    private static string Callback(Uri browser, string code = "valid-code")
        => "/callback?state=" + Query(browser.Query)["state"] + "&code=" + Uri.EscapeDataString(code);
    private static Uri Redirect(Uri browser) => new(Query(browser.Query)["redirect_uri"]);
    private static async Task<string> Send(Uri redirect, string target, CancellationToken token, string? headers = null)
        => await SendRaw(redirect, $"GET {target} HTTP/1.1\r\n" + (headers ?? $"Host: {redirect.Authority}\r\n") + "\r\n", token);
    private static async Task<string> SendRaw(Uri redirect, string request, CancellationToken token)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, redirect.Port, token);
        await socket.GetStream().WriteAsync(Encoding.Latin1.GetBytes(request), token);
        using var reader = new StreamReader(socket.GetStream(), Encoding.UTF8);
        return await reader.ReadToEndAsync(token);
    }
    private static Task PortIsReleased(int port)
    {
        using var replacement = new SpotifyCallbackListener(port);
        Assert.Equal(port, new Uri(replacement.RedirectUri).Port);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ValidCallbackExchangesPkceBeforeShowingSuccessAndCapturesClientId()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault();
        Uri? browser = null;
        var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://accounts.spotify.com/api/token", request.RequestUri!.AbsoluteUri);
            var fields = Query(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(ClientId, fields["client_id"]);
            Assert.Equal("authorization_code", fields["grant_type"]);
            Assert.Equal("code+with space/", fields["code"]);
            Assert.Equal(Query(browser!.Query)["redirect_uri"], fields["redirect_uri"]);
            Assert.Equal(Query(browser.Query)["code_challenge"], SpotifyClient.Challenge(fields["code_verifier"]));
            Assert.InRange(fields["code_verifier"].Length, 43, 128);
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) };
        });
        using var spotify = Create(vault, handler);
        var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        Assert.NotNull(browser); Assert.Equal("accounts.spotify.com", browser.Host);
        Assert.Equal("S256", Query(browser.Query)["code_challenge_method"]);
        spotify.ClientId = new string('a', 32);
        var response = await Send(Redirect(browser), Callback(browser, "code+with space/"), deadline.Token);
        await connect;
        Assert.Contains("200 OK", response); Assert.Contains("Musync is connected", response);
        Assert.Contains("Cache-Control: no-store", response); Assert.Contains("Referrer-Policy: no-referrer", response);
        Assert.True(spotify.Connected); Assert.Equal(1, vault.Saves); Assert.Equal(1, handler.Calls);
        var saved = JsonSerializer.Deserialize<SpotifyTokens>(vault.Json!);
        Assert.Equal("test-access", saved!.AccessToken); Assert.Equal("test-refresh", saved.RefreshToken);
        await PortIsReleased(Redirect(browser).Port);
    }

    public static IEnumerable<object[]> InvalidTargets => new[]
    {
        "/callback?state=wrong&code=code",
        "/callback?state=wrong&error=access_denied",
        "/callback?state={state}&state={state}&code=code",
        "/callback?state={state}&%73tate={state}&code=code",
        "/callback?state={state}&code=one&code=two",
        "/callback?state={state}&code=code&error=access_denied",
        "/callback?state={state}&code=",
        "/callback?state={state}&code=%GG",
        "/callback?state={state}&code=%00",
        "/callback?state={state}&code=%FF",
        "/callback?state={state}",
        "http://127.0.0.1/callback?state={state}&code=code",
        "//callback?state={state}&code=code",
        "/other/../callback?state={state}&code=code",
        "/callback?state={state}&code=code#fragment"
    }.Select(target => new object[] { target });

    [Theory]
    [MemberData(nameof(InvalidTargets))]
    public async Task InvalidCallbacksDoNotConsumeSignIn(string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        var invalid = await Send(redirect, target.Replace("{state}", Query(browser!.Query)["state"]), deadline.Token);
        Assert.DoesNotContain("Musync is connected", invalid); Assert.False(connect.IsCompleted);
        Assert.False(spotify.Connected); Assert.Equal(0, handler.Calls); Assert.Equal(0, vault.Saves);
        var valid = await Send(redirect, Callback(browser), deadline.Token);
        await connect; Assert.Contains("200 OK", valid); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Host: foreign.example\r\n")]
    [InlineData("Host: {host}\r\nHost: {host}\r\n")]
    [InlineData("Host: {host}\r\nTransfer-Encoding: chunked\r\n")]
    [InlineData("Host: {host}\r\nContent-Length: 1\r\n")]
    [InlineData("Host: {host}\r\n Bad: folded\r\n")]
    public async Task InvalidHeadersAreRejectedThenAValidCallbackWorks(string headers)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        var invalid = await Send(redirect, Callback(browser!), deadline.Token, headers.Replace("{host}", redirect.Authority));
        Assert.Contains("400 Bad Request", invalid); Assert.Equal(0, handler.Calls);
        await Send(redirect, Callback(browser!), deadline.Token); await connect;
        Assert.True(spotify.Connected);
    }

    [Fact]
    public async Task OversizedRequestDoesNotAllocateAnUnboundedLineOrEndSignIn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        var invalid = await SendRaw(redirect, "GET /" + new string('x', 9000) + " HTTP/1.1\r\nHost: " + redirect.Authority + "\r\n\r\n", deadline.Token);
        Assert.Contains("400 Bad Request", invalid); Assert.Equal(0, handler.Calls);
        await Send(redirect, Callback(browser!), deadline.Token); await connect;
    }

    [Fact]
    public async Task StalledAndTruncatedConnectionsDoNotPreventTheNextCallback()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        using (var truncated = new TcpClient())
        {
            await truncated.ConnectAsync(IPAddress.Loopback, redirect.Port, deadline.Token);
            await truncated.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /callback"), deadline.Token);
        }
        using var stalled = new TcpClient(); await stalled.ConnectAsync(IPAddress.Loopback, redirect.Port, deadline.Token);
        var response = await Send(redirect, Callback(browser!), deadline.Token); await connect;
        Assert.Contains("200 OK", response); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FaviconIsNotAnAuthorizationAndDoesNotConsumeSignIn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        Assert.Contains("404 Not Found", await Send(Redirect(browser!), "/favicon.ico", deadline.Token));
        Assert.Equal(0, handler.Calls);
        await Send(Redirect(browser!), Callback(browser!), deadline.Token); await connect;
    }

    [Fact]
    public async Task DenialRespondsToBrowserAndReleasesListenerWithoutExchangingTokens()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var response = await Send(Redirect(browser!), "/callback?state=" + Query(browser!.Query)["state"] + "&error=access_denied", deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        Assert.Contains("Spotify was not connected", response); Assert.Equal(0, handler.Calls);
        Assert.False(spotify.Connected); Assert.Null(vault.Json); await PortIsReleased(Redirect(browser).Port);
    }

    [Theory]
    [InlineData("invalid-private-token-response")]
    [InlineData("{\"access_token\":null,\"refresh_token\":\"r\",\"token_type\":\"Bearer\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Basic\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [InlineData("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"token_type\":\"Bearer\",\"expires_in\":\"3600\"}")]
    [InlineData("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":3600}")]
    public async Task InvalidTokenResponseCannotSaveOrShowConnected(string tokens)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokens) }));
        using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var response = await Send(Redirect(browser!), Callback(browser!), deadline.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => connect);
        Assert.Contains("502 Bad Gateway", response); Assert.DoesNotContain("Musync is connected", response);
        Assert.False(spotify.Connected); Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task TokenExchangeFailureShowsFailureAndTheClientCanSignInAgain()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var attempt = 0;
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(++attempt == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(attempt == 1 ? "{}" : ValidTokens) }));
        using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var failed = await Send(Redirect(browser!), Callback(browser!), deadline.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => connect);
        Assert.Contains("502 Bad Gateway", failed); Assert.False(spotify.Connected);
        connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        Assert.Contains("Musync is connected", await Send(Redirect(browser!), Callback(browser!), deadline.Token));
        await connect; Assert.Equal(1, vault.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationOrDisconnectReleasesListener(bool disconnect)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, cancellation.Token);
        if (disconnect) spotify.Disconnect(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(spotify.Connected); Assert.Null(vault.Json); Assert.Equal(0, handler.Calls);
        await PortIsReleased(Redirect(browser!).Port);
    }

    [Fact]
    public async Task DisconnectDuringExchangeCannotRestoreTokens()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new Vault();
        var handler = new Handler(async (_, _) => { entered.SetResult(); await release.Task.WaitAsync(deadline.Token);
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) }; });
        using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var response = Send(Redirect(browser!), Callback(browser!), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token); spotify.Disconnect(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect); await response;
        Assert.False(spotify.Connected); Assert.Null(vault.Json); Assert.Equal(0, vault.Saves);
    }

    [Fact]
    public async Task ConcurrentSignInDoesNotOpenAnotherBrowserOrListener()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, cancellation.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => spotify.ConnectAsync(_ => throw new Exception("Second browser opened"), cancellation.Token));
        await Send(Redirect(browser!), Callback(browser!), cancellation.Token); await connect;
    }

    [Fact]
    public async Task TimeoutAndBrowserLaunchFailureReleaseTheListener()
    {
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler, TimeSpan.FromMilliseconds(100));
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => connect); await PortIsReleased(Redirect(browser!).Port);
        var failure = spotify.ConnectAsync(uri => { browser = uri; throw new InvalidOperationException("Browser unavailable"); }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure); await PortIsReleased(Redirect(browser!).Port);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BrowserSuccessWaitsForTheTokenExchange()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new Vault(); var handler = new Handler(async (_, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) };
        });
        using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var response = Send(Redirect(browser!), Callback(browser!), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Assert.False(response.IsCompleted); Assert.False(spotify.Connected); Assert.Equal(0, vault.Saves);
        release.SetResult(); Assert.Contains("Musync is connected", await response); await connect;
    }

    [Fact]
    public async Task FragmentedHeadersAndCodeEncodingAreHandled()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, redirect.Port, deadline.Token);
        var request = Encoding.ASCII.GetBytes("GET " + Callback(browser!) + " HTTP/1.1\r\nHost: " + redirect.Authority + "\r\nContent-Length: 0\r\n\r\n");
        for (var i = 0; i < request.Length; i += 3)
            await socket.GetStream().WriteAsync(request.AsMemory(i, Math.Min(3, request.Length - i)), deadline.Token);
        using var reader = new StreamReader(socket.GetStream());
        Assert.Contains("Musync is connected", await reader.ReadToEndAsync(deadline.Token)); await connect;
    }

    [Fact]
    public async Task ExcessiveHeadersAreBoundedAndDoNotConsumeSignIn()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); using var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        var redirect = Redirect(browser!);
        try
        {
            var response = await SendRaw(redirect, "GET /callback HTTP/1.1\r\nHost: " + redirect.Authority + "\r\nLong: " + new string('x', 16384), deadline.Token);
            Assert.Contains("400 Bad Request", response);
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // Closing a bounded request with unread bytes may reset TCP.
        }
        Assert.Equal(0, handler.Calls); Assert.False(connect.IsCompleted); Assert.False(spotify.Connected);
        await Send(redirect, Callback(browser!), deadline.Token); await connect;
    }

    [Fact]
    public async Task DisposeCancelsSignInAndReleasesItsPort()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault(); var handler = SuccessfulHandler(); var spotify = Create(vault, handler);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        spotify.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await PortIsReleased(Redirect(browser!).Port);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => spotify.ConnectAsync(_ => { }, deadline.Token));
    }

    [Fact]
    public async Task OccupiedCallbackPortDoesNotOpenTheBrowser()
    {
        using var listener = new SpotifyCallbackListener(0);
        var vault = new Vault(); var handler = SuccessfulHandler();
        using var spotify = new SpotifyClient(vault, new HttpClient(handler), new Uri(listener.RedirectUri).Port) { ClientId = ClientId };
        await Assert.ThrowsAsync<SocketException>(() => spotify.ConnectAsync(_ => throw new Exception("Browser opened"), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RefreshCompletingAfterDisconnectCannotRestoreTheAccount()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new Vault { Json = JsonSerializer.Serialize(new SpotifyTokens("expired", "refresh", DateTime.MinValue)) };
        var handler = new Handler(async (_, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) };
        });
        using var spotify = Create(vault, handler);
        var devices = spotify.DevicesAsync(deadline.Token);
        await entered.Task.WaitAsync(deadline.Token); spotify.Disconnect(); release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => devices);
        Assert.False(spotify.Connected); Assert.Null(vault.Json); Assert.Equal(0, vault.Saves); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RefreshKeepsAnExistingRefreshTokenWhenSpotifyOmitsIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var vault = new Vault { Json = JsonSerializer.Serialize(new SpotifyTokens("expired", "original-refresh", DateTime.MinValue)) };
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "accounts.spotify.com")
            {
                var form = Query(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("original-refresh", form["refresh_token"]);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"new-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}") };
            }
            Assert.Equal("new-access", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"devices\":[]}") };
        });
        using var spotify = Create(vault, handler);
        Assert.Empty(await spotify.DevicesAsync(deadline.Token));
        Assert.True(spotify.Connected); Assert.Equal("original-refresh", JsonSerializer.Deserialize<SpotifyTokens>(vault.Json!)!.RefreshToken);
    }

    [Fact]
    public async Task ANewSignInCannotBeOverwrittenByAnOlderAccountRefresh()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new Vault { Json = JsonSerializer.Serialize(new SpotifyTokens("old-access", "old-refresh", DateTime.MinValue)) };
        var handler = new Handler(async (request, token) =>
        {
            var form = Query(await request.Content!.ReadAsStringAsync(token));
            if (form["grant_type"] == "refresh_token")
            {
                entered.SetResult(); await release.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens.Replace("test-access", "old-refreshed")) };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(ValidTokens) };
        });
        using var spotify = Create(vault, handler);
        var previousRequest = spotify.DevicesAsync(deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        Uri? browser = null; var connect = spotify.ConnectAsync(uri => browser = uri, deadline.Token);
        await Send(Redirect(browser!), Callback(browser!), deadline.Token); await connect;
        release.SetResult(); await Assert.ThrowsAsync<InvalidOperationException>(() => previousRequest);
        Assert.True(spotify.Connected); Assert.Equal(1, vault.Saves);
        Assert.Equal("test-access", JsonSerializer.Deserialize<SpotifyTokens>(vault.Json!)!.AccessToken);
    }
}
