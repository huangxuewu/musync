using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Musync.App;
using Xunit;

namespace Musync.Tests;

public sealed partial class SpotifyPlaybackTests
{
    [AvaloniaTheory]
    [InlineData("en", 1460, 940)]
    [InlineData("zh-Hans", 1460, 940)]
    [InlineData("ja", 1120, 720)]
    public async Task FolderImportUsesAnOverlayInsteadOfThePermanentBottomMessage(string language, int width, int height)
    {
        await using var fixture = new PresentationFixture(language);
        var folder = Path.Combine(fixture.DirectoryPath, "原創フォルダー"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "One.mp3"), "mock");
        var window = new MainWindow { DataContext = fixture.Model, Width = width, Height = height };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            Assert.False(fixture.Model.HasToast);
            await fixture.Model.ImportFoldersAsync([folder]);
            using var frame = window.CaptureRenderedFrame();
            var toast = window.FindControl<Border>("StatusToast")!;
            var text = window.FindControl<TextBlock>("StatusToastText")!;
            Assert.True(toast.IsEffectivelyVisible);
            Assert.Equal(string.Format(fixture.Model.Text["FoldersImported"], 1, 1), text.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));
            var point = toast.TranslatePoint(new Point(), window)!.Value;
            Assert.True(point.X >= 8 && point.X + toast.Bounds.Width <= width - 8);
            Assert.True(point.Y >= 8 && point.Y + toast.Bounds.Height <= height - 104);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == text.Text);
            var hit = window.InputHitTest(new Point(point.X + 20, point.Y + 15)) as Visual;
            Assert.NotSame(text, hit); Assert.NotSame(toast, hit);
            Assert.False(hit?.GetVisualAncestors().Contains(toast) ?? false);
            SaveReviewFrame(frame, $"toast-{language}-{width}-import.png");
            fixture.Model.ShowError(new InvalidOperationException(new string('測', 90)));
            using var longFrame = window.CaptureRenderedFrame();
            Assert.True(text.Bounds.Height > 20);
            Assert.True(toast.Bounds.Height >= text.Bounds.Height + 24);
            Assert.InRange(toast.Bounds.Width, 1, 440);
            if (language == "ja") SaveReviewFrame(longFrame, "toast-ja-1120-long-message.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task IdenticalReimportRestartsFiveSecondsAndExpiredToastLeavesNoMessageInTheWorkspace()
    {
        await using var fixture = new PresentationFixture("zh-Hans");
        var folder = Path.Combine(fixture.DirectoryPath, "Import"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "One.mp3"), "mock");
        var window = new MainWindow { DataContext = fixture.Model, Width = 1120, Height = 720 };
        window.Show(); window.ApplyMotionPreference(false);
        try
        {
            await fixture.Model.ImportFoldersAsync([folder]);
            await fixture.Model.ImportFoldersAsync([folder]);
            var repeatedMessage = fixture.Model.ToastMessage;
            await Task.Delay(2800);
            await fixture.Model.ImportFoldersAsync([folder]);
            Assert.Equal(repeatedMessage, fixture.Model.ToastMessage);
            await Task.Delay(2500);
            Assert.True(fixture.Model.HasToast); // The older five-second deadline has passed.
            await Task.Delay(2700);
            Assert.False(fixture.Model.HasToast); Assert.Empty(fixture.Model.ToastMessage);
            Assert.Equal(fixture.Model.Text["Idle"], fixture.Model.Status);
            using var frame = window.CaptureRenderedFrame();
            Assert.False(window.FindControl<Border>("StatusToast")!.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == repeatedMessage);
            SaveReviewFrame(frame, "toast-zh-Hans-1120-expired.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NormalPlaybackDoesNotCreateToastsAndDisposalClearsAnActiveNotice()
    {
        var fixture = new PresentationFixture();
        try
        {
            Assert.False(fixture.Model.HasToast);
            await fixture.Model.PlayTrackAsync(fixture.First);
            Assert.True(fixture.Model.IsPlaying); Assert.False(fixture.Model.HasToast);
            fixture.Model.ShowError(new InvalidOperationException("Original test failure"));
            Assert.True(fixture.Model.HasToast);
            await fixture.Model.DisposeAsync();
            Assert.False(fixture.Model.HasToast);
        }
        finally { Directory.Delete(fixture.DirectoryPath, true); }
    }
}
