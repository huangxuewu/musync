using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Net;
using System.Text.Json;
using Musync.App;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static string PlaybackWithVolume(int? volume = 65, bool supported = true, string? id = "active-device", bool restricted = false) =>
        PlaybackJson().Replace("\"item\":", "\"device\":" + JsonSerializer.Serialize(new
        { id, volume_percent = volume, supports_volume = supported, is_restricted = restricted }) + ",\"item\":");

    [AvaloniaFact]
    public async Task SnapshotVolumeIsObservedWithoutWritingAndExternalChangesUpdateThePlayer()
    {
        var volume = 65;
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume(volume)));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        Assert.True(fixture.Model.CanAdjustVolume); Assert.Equal("65%", fixture.Model.PlayerVolumeText);
        Assert.Equal("volume-2", fixture.Model.VolumeIcon);
        volume = 35; await fixture.Model.RefreshSpotifyPlaybackAsync();
        Assert.Equal(35, fixture.Model.PlayerVolume); Assert.Equal("35%", fixture.Model.PlayerVolumeText);
        Assert.Equal(65, fixture.Model.MasterVolume);
    }

    [AvaloniaFact]
    public async Task RapidSliderEditsCoalesceAndTargetTheObservedPlayingDevice()
    {
        var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume(id: "active /device")));
            Assert.Equal(HttpMethod.Put, request.Method); paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.PlayerVolume = 10; fixture.Model.PlayerVolume = 20; fixture.Model.PlayerVolume = 25;
        await fixture.Model.WaitForVolumeAsync();
        Assert.Equal(["/v1/me/player/volume?volume_percent=25&device_id=active%20%2Fdevice"], paths);
        Assert.Equal(25, fixture.Model.PlayerVolume); Assert.Equal(65, fixture.Model.MasterVolume);
    }

    [AvaloniaFact]
    public async Task SpotifyMuteAndUnmuteRestoreThePreviousVolume()
    {
        var paths = new List<string>();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume()));
            paths.Add(request.RequestUri!.PathAndQuery); return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.MuteCommand.Execute(null); await CommandFinished(fixture.Model.MuteCommand);
        Assert.True(fixture.Model.PlayerMuted); Assert.Equal("volume-x", fixture.Model.VolumeIcon);
        Assert.Equal(fixture.Model.Text["PlayerUnmute"], fixture.Model.MuteLabel);
        fixture.Model.MuteCommand.Execute(null); await CommandFinished(fixture.Model.MuteCommand);
        Assert.False(fixture.Model.PlayerMuted); Assert.Equal(65, fixture.Model.PlayerVolume);
        Assert.Equal(["/v1/me/player/volume?volume_percent=0&device_id=active-device", "/v1/me/player/volume?volume_percent=65&device_id=active-device"], paths);
    }

    [AvaloniaFact]
    public async Task LocalVolumeAndMuteUseMasterGainWithoutSendingSpotifyCommands()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("No Spotify command expected."));
        await using var fixture = new Fixture(handler);
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
        fixture.Model.PlayerVolume = 42;
        Assert.Equal(42, fixture.Model.MasterVolume);
        fixture.Model.MuteCommand.Execute(null); await CommandFinished(fixture.Model.MuteCommand);
        Assert.Equal(0, fixture.Model.MasterVolume);
        fixture.Model.MuteCommand.Execute(null); await CommandFinished(fixture.Model.MuteCommand);
        Assert.Equal(42, fixture.Model.MasterVolume); Assert.Equal(0, handler.Calls);
    }

    [AvaloniaTheory]
    [InlineData(false, false, 65, "active-device")]
    [InlineData(true, true, 65, "active-device")]
    [InlineData(true, false, null, "active-device")]
    [InlineData(true, false, 65, null)]
    public async Task UnsupportedRestrictedOrUnknownVolumeNeverSendsACommand(bool supported, bool restricted, int? volume, string? id)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume(volume, supported, id, restricted)));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        Assert.False(fixture.Model.CanAdjustVolume);
        Assert.Equal(fixture.Model.Text["SpotifyVolumeUnavailable"], fixture.Model.VolumeHint);
        fixture.Model.PlayerVolume = 10; fixture.Model.MuteCommand.Execute(null); await CommandFinished(fixture.Model.MuteCommand);
        Assert.Equal(volume ?? 0, fixture.Model.PlayerVolume); Assert.Equal(1, handler.Calls);
    }

    [AvaloniaFact]
    public async Task FailedSpotifyWriteRestoresConfirmedVolumeAndReportsTheLocalizedError()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, PlaybackWithVolume())
            : Response(HttpStatusCode.Forbidden, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}")));
        await using var fixture = new Fixture(handler, "ja", observePlayback: true);
        fixture.Model.PlayerVolume = 10; await fixture.Model.WaitForVolumeAsync();
        Assert.Equal(65, fixture.Model.PlayerVolume);
        Assert.Contains(fixture.Model.Text["SpotifyPremiumRequired"], fixture.Model.Status);
    }

    [AvaloniaFact]
    public async Task ChangingSourceCancelsAQueuedVolumeEditAndPreservesLocalGain()
    {
        var writes = 0;
        var handler = new Handler((request, _) =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume()));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        fixture.Model.PlayerVolume = 10;
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
        await fixture.Model.WaitForVolumeAsync();
        Assert.Equal(0, writes); Assert.Equal(65, fixture.Model.PlayerVolume);
        Assert.Equal(65, fixture.Model.MasterVolume);
    }

    [AvaloniaFact]
    public async Task PendingWritesAreSerializedAndOldSnapshotsCannotMoveTheThumbBack()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        var handler = new Handler((request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume()));
            writes.Add(request.RequestUri!.Query);
            if (writes.Count == 1) { started.SetResult(); return first.Task; }
            Assert.True(first.Task.IsCompleted); Assert.False(token.IsCancellationRequested);
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        try
        {
            fixture.Model.PlayerVolume = 30;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Model.PlayerVolume = 40;
            await fixture.Model.RefreshSpotifyPlaybackAsync();
            Assert.Equal(40, fixture.Model.PlayerVolume);
            await Task.Delay(200); Assert.Single(writes);
            first.SetResult(Response(HttpStatusCode.NoContent));
            await fixture.Model.WaitForVolumeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, writes.Count); Assert.Contains("volume_percent=40", writes[1]);
            Assert.Equal(40, fixture.Model.PlayerVolume);
        }
        finally { first.TrySetResult(Response(HttpStatusCode.NoContent)); }
    }

    [AvaloniaFact]
    public async Task ReceiverChangeDiscardsAnInFlightDevicesResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = PlaybackWithVolume();
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, state));
            started.SetResult(); return response.Task;
        });
        await using var fixture = new Fixture(handler, observePlayback: true);
        try
        {
            fixture.Model.PlayerVolume = 20; await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            state = PlaybackWithVolume(80, id: "other-device");
            await fixture.Model.RefreshSpotifyPlaybackAsync();
            response.SetResult(Response(HttpStatusCode.NoContent)); await fixture.Model.WaitForVolumeAsync();
            Assert.Equal(80, fixture.Model.PlayerVolume); Assert.Equal(65, fixture.Model.MasterVolume);
        }
        finally { response.TrySetResult(Response(HttpStatusCode.NoContent)); }
    }

    [Fact]
    public async Task VolumeEndpointRejectsOutOfRangeValuesBeforeSendingAnything()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var client = Client(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.VolumeAsync(-1, "device", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.VolumeAsync(101, "device", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [AvaloniaTheory]
    [InlineData("en", 1120, 720)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("ja", 1120, 720)]
    public async Task VolumeControlsFitAndWorkInFullAndMiniPlayers(string language, int width, int height)
    {
        var actualVolume = 65; var writes = 0;
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume(actualVolume)));
            writes++; actualVolume = int.Parse(request.RequestUri!.Query.Split("volume_percent=")[1].Split('&')[0]);
            return Task.FromResult(Response(HttpStatusCode.NoContent));
        });
        await using var fixture = new Fixture(handler, language, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show();
        try
        {
            var slider = window.FindControl<Slider>("PlayerVolumeSlider")!;
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.True(slider.IsEnabled); Assert.Equal(65, slider.Value);
                var origin = slider.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(origin.X >= 0 && origin.X + slider.Bounds.Width <= window.ClientSize.Width);
                var utility = window.FindControl<Button>("MiniPlayerButton")!;
                var utilityOrigin = utility.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(utilityOrigin.X + utility.Bounds.Width <= window.ClientSize.Width);
                SaveReviewFrame(frame, $"volume-{language}-{width}-full.png");
            }
            slider.Focus(); window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
            await fixture.Model.WaitForVolumeAsync(); Assert.Equal(100, actualVolume);
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var frame = window.CaptureRenderedFrame()) SaveReviewFrame(frame, $"volume-{language}-mini.png");
            var toggle = window.FindControl<ToggleButton>("MiniVolumeToggle")!;
            toggle.Focus(); window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.True(window.FindControl<Popup>("MiniVolumePopover")!.IsOpen);
                Assert.True(window.FindControl<Slider>("MiniVolumeSlider")!.IsFocused);
                SaveReviewFrame(frame, $"volume-{language}-mini-open.png");
            }
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.None);
            await fixture.Model.WaitForVolumeAsync(); Assert.Equal(0, actualVolume); Assert.True(fixture.Model.PlayerMuted);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(window.FindControl<Popup>("MiniVolumePopover")!.IsOpen); Assert.True(toggle.IsFocused);
            Assert.Equal(2, writes);
            actualVolume = 45; await fixture.Model.RefreshSpotifyPlaybackAsync();
            Assert.Equal(45, window.FindControl<Slider>("PlayerVolumeSlider")!.Value);
            Assert.Equal("45%", fixture.Model.PlayerVolumeText);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    public async Task MiniUnsupportedVolumeShowsLocalizedRecoveryAndKeepsTransportAccessible(string language)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, PlaybackWithVolume(null, supported: false))));
        await using var fixture = new Fixture(handler, language, observePlayback: true);
        var window = new MainWindow { DataContext = fixture.Model };
        window.Show();
        try
        {
            using var initial = window.CaptureRenderedFrame();
            window.FindControl<Button>("MiniPlayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var mini = window.CaptureRenderedFrame();
            window.FindControl<ToggleButton>("MiniVolumeToggle")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await Task.Yield();
            using var frame = window.CaptureRenderedFrame();
            Assert.True(window.FindControl<Popup>("MiniVolumePopover")!.IsOpen);
            Assert.False(window.FindControl<Slider>("MiniVolumeSlider")!.IsEnabled);
            Assert.Equal("—", fixture.Model.PlayerVolumeText); Assert.True(fixture.Model.IsPlaying);
            SaveReviewFrame(frame, $"volume-{language}-mini-unavailable.png");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(window.FindControl<Popup>("MiniVolumePopover")!.IsOpen);
            Assert.True(window.FindControl<ToggleButton>("MiniVolumeToggle")!.IsFocused);
        }
        finally { window.Close(); }
    }
}
