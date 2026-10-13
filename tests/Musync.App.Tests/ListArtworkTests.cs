using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Musync.App;
using Musync.App.Controls;
using Musync.Core;
using System.Net;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    private static byte[] ListCover()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(96, 96));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            drawing.FillRectangle(new SolidColorBrush(Color.Parse("#CB374F")), new Rect(0, 0, 96, 96));
            drawing.FillRectangle(Brushes.White, new Rect(12, 12, 72, 12));
            drawing.FillRectangle(new SolidColorBrush(Color.Parse("#251A35")), new Rect(12, 36, 72, 48));
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions()); return stream.ToArray();
    }
    private static string ListTrack(int index) => TrackJson.Replace("\"song\"", "\"song" + index + "\"")
        .Replace("spotify:track:song", "spotify:track:song" + index)
        .Replace("\"images\":[]", "\"images\":[{\"url\":\"https://i.scdn.co/image/cover" + index + "\"}]");
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1120, 720)]
    [InlineData("ja", 1120, 720)]
    public async Task ListCoversLoadAutomaticallyOnlyForVisibleRowsAndScrollLoadsMore(string language, int width, int height)
    {
        var downloads = new HashSet<string>(); var cover = ListCover();
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.spotify.com")
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + string.Join(",", Enumerable.Range(0, 30).Select(ListTrack)) + "]}}"));
            Assert.Null(request.Headers.Authorization); downloads.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) });
        });
        await using var fixture = new Fixture(handler, language);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        try
        {
            window.Show(); window.CaptureRenderedFrame()?.Dispose();
            await Until(() => fixture.Model.Tracks[0].HasArtwork);
            Assert.InRange(downloads.Count, 1, 20); Assert.False(fixture.Model.Tracks[29].HasArtwork);
            var image = window.GetVisualDescendants().OfType<TrackArtworkImage>().First();
            Assert.Equal(48, image.Bounds.Width); Assert.Equal(48, image.Bounds.Height); Assert.Equal(Stretch.Uniform, image.Stretch);
            Assert.NotNull(image.Source);
            var origin = image.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.InRange(origin.X, 0, width - 48); Assert.InRange(origin.Y, 0, height - 48);
            var attribution = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.DataContext == fixture.Model.Tracks[0] && b.Command == fixture.Model.Tracks[0].OpenArtworkSource);
            Assert.True(attribution.IsVisible); Assert.Equal(48, attribution.Bounds.Height);
            Assert.Equal(fixture.Model.Text["ArtworkSourceSpotify"], AutomationProperties.GetName(attribution));
            using (var frame = window.CaptureRenderedFrame()) SaveReviewFrame(frame, $"artwork-{language}-{width}-top.png");
            var scroll = image.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height); window.CaptureRenderedFrame()?.Dispose();
            await Until(() => fixture.Model.Tracks[29].HasArtwork);
            Assert.Contains("/image/cover29", downloads);
            using var bottom = window.CaptureRenderedFrame(); SaveReviewFrame(bottom, $"artwork-{language}-{width}-bottom.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FilterChangeDiscardsLateArtworkAndReusesSuccessfulCoverOnReturn()
    {
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cover = ListCover(); var slow = true;
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.spotify.com") return Task.FromResult(Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + ListTrack(0) + "]}}"));
            if (slow) { entered.TrySetResult(); return late.Task; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) });
        });
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var previous = fixture.Model.Tracks[0]; var loading = previous.EnsureArtworkAsync(); await entered.Task;
        fixture.Model.Search = "next"; // Clear results and dispose the old artwork owner.
        slow = false; late.SetResult(new(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) }); await loading;
        Assert.False(previous.HasArtwork);
        fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var next = fixture.Model.Tracks[0]; await next.EnsureArtworkAsync(); Assert.True(next.HasArtwork);
        var calls = handler.Calls;
        fixture.Model.Navigation.Execute("library"); await CommandFinished(fixture.Model.Navigation);
        await fixture.Model.Tracks[0].EnsureArtworkAsync();
        Assert.True(fixture.Model.Tracks[0].HasArtwork); Assert.Equal(calls, handler.Calls);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenListCoverKeepsFallbackAndDoesNotChangePlayerOrToast(bool corrupt)
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
            ? Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + ListTrack(0) + "]}}")
            : corrupt ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }
            : Response(HttpStatusCode.ServiceUnavailable)));
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var status = fixture.Model.Status; var title = fixture.Model.Title; var toast = fixture.Model.ToastMessage;
        await fixture.Model.Tracks[0].EnsureArtworkAsync();
        Assert.False(fixture.Model.Tracks[0].HasArtwork); Assert.Equal(status, fixture.Model.Status);
        Assert.Equal(title, fixture.Model.Title); Assert.Equal(toast, fixture.Model.ToastMessage);
    }

    [AvaloniaFact]
    public async Task LocalListMatchesSpotifyWithoutSwitchingSourceOrWritingCatalogAssociations()
    {
        var cover = ListCover();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
            ? Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + ListTrack(0) + "]}}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) }));
        await using var fixture = new Fixture(handler);
        fixture.Model.Source = fixture.Model.Sources.First(s => s.Id == "local");
        using var row = new TrackViewModel(new("local:example", "Song", "Artist", "missing-song.mp3", DurationSeconds: 180), fixture.Model);
        await row.EnsureArtworkAsync();
        Assert.True(row.HasArtwork); Assert.True(fixture.Model.IsLocal); Assert.False(fixture.Model.IsPlaying);
        Assert.Equal("local:example", row.Track.Id); Assert.Null(row.Track.SpotifyUri); Assert.Null(row.Track.ImageUrl);
    }

    [AvaloniaFact]
    public async Task DisconnectRemovesListCoversAndCancelsTheirOwners()
    {
        var cover = ListCover();
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "api.spotify.com"
            ? Response(HttpStatusCode.OK, "{\"tracks\":{\"items\":[" + ListTrack(0) + "]}}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) }));
        await using var fixture = new Fixture(handler);
        fixture.Model.Search = "song"; fixture.Model.SearchCommand.Execute(null); await CommandFinished(fixture.Model.SearchCommand);
        var row = fixture.Model.Tracks[0]; await row.EnsureArtworkAsync(); Assert.True(row.HasArtwork);
        fixture.Model.DisconnectSpotify.Execute(null); await CommandFinished(fixture.Model.DisconnectSpotify);
        Assert.False(row.HasArtwork); await fixture.Model.Tracks[0].EnsureArtworkAsync();
        Assert.False(fixture.Model.Tracks[0].HasArtwork);
    }
}
