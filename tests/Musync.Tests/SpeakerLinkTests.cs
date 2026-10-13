using Musync.Core;
using Xunit;

namespace Musync.Tests;

public sealed class SpeakerLinkTests
{
    [Fact]
    public void DraftChangesDoNotModifySavedMembershipOrLevels()
    {
        var saved = Link("Home", "desk", 42, 120);
        var draft = SpeakerLinks.Draft(saved);
        draft.Name = "Night"; draft.MasterVolume = 25;
        draft.Speakers["desk"].Preferences.Volume = 12;
        draft.Speakers["desk"].Preferences.DelayMs = 700;
        draft.Speakers.Clear();
        Assert.Equal("Home", saved.Name);
        Assert.Equal(65, saved.MasterVolume);
        Assert.Equal(42, saved.Speakers["desk"].Preferences.Volume);
        Assert.Equal(120, saved.Speakers["desk"].Preferences.DelayMs);
    }

    [Fact]
    public void PlaybackUsesOnlyCurrentAvailableMembersOfTheSelectedLink()
    {
        var saved = Link("Home", "desk", 42, 120);
        saved.Speakers["offline"] = new() { Name = "HomePod", Transport = SpeakerTransport.AirPlay };
        saved.Speakers["locked"] = new() { Name = "Locked receiver" };
        var routes = SpeakerLinks.Resolve(saved, [
            new("desk", "Desk", SpeakerTransport.Windows),
            new("unlinked", "Other", SpeakerTransport.Windows),
            new("locked", "Locked receiver", SpeakerTransport.AirPlay, UnsupportedReason: "AirPlayAuthentication")]);
        var route = Assert.Single(routes);
        Assert.Equal("desk", route.Device.Id);
        Assert.Equal(42, route.Preferences.Volume);
        Assert.Equal(120, route.Preferences.DelayMs);
        Assert.True(route.Preferences.Selected);
        Assert.Contains("offline", saved.Speakers.Keys);
    }

    [Fact]
    public void AReceiverAdvertisedOnTwoTransportsIsRoutedOnce()
    {
        var saved = Link("Home", "airplay", 42, 120);
        saved.Speakers["cast"] = new() { Name = "Receiver", Transport = SpeakerTransport.Chromecast };
        var route = Assert.Single(SpeakerLinks.Resolve(saved, [
            new("airplay", "Receiver", SpeakerTransport.AirPlay, "receiver.local", 7000),
            new("cast", "Receiver", SpeakerTransport.Chromecast, "receiver.local", 8009)]));
        Assert.Equal("airplay", route.Device.Id);
    }

    [Fact]
    public void LegacySelectionMigratesOnceAndRetainsLevels()
    {
        var settings = new AppSettings { MasterVolume = 31 };
        settings.Speakers["desk"] = new() { Selected = true, Volume = 42, DelayMs = 120, Muted = true };
        settings.Speakers["other"] = new() { Selected = false };
        SpeakerLinks.MigrateLegacy(settings, "Main link");
        SpeakerLinks.MigrateLegacy(settings, "Another name");
        var link = Assert.Single(settings.Links);
        Assert.Equal(link.Id, settings.ActiveLinkId);
        Assert.Single(link.Speakers);
        Assert.Equal(31, link.MasterVolume);
        Assert.Equal(42, link.Speakers["desk"].Preferences.Volume);
        Assert.Equal(120, link.Speakers["desk"].Preferences.DelayMs);
        Assert.True(link.Speakers["desk"].Preferences.Muted);
        settings.Speakers["desk"].Volume = 90;
        Assert.Equal(42, link.Speakers["desk"].Preferences.Volume);
    }

    [Fact]
    public void FreshInstallDoesNotInventAPlaybackLink()
    {
        var settings = new AppSettings();
        SpeakerLinks.MigrateLegacy(settings, "Main link");
        Assert.Empty(settings.Links);
        Assert.Null(settings.ActiveLinkId);
        Assert.True(settings.LinksInitialized);
    }

    [Fact]
    public void InvalidNamesAndEmptyMembershipAreRejected()
    {
        var existing = Link("Home", "desk", 65, 0);
        var draft = Link(" home ", "desk", 65, 0);
        Assert.Equal("LinkNameExists", SpeakerLinks.ValidationError(draft, [existing]));
        draft.Name = " "; Assert.Equal("LinkNameRequired", SpeakerLinks.ValidationError(draft, [existing]));
        draft.Name = new string('x', 49); Assert.Equal("LinkNameRequired", SpeakerLinks.ValidationError(draft, []));
        draft.Name = "Bedroom"; draft.Speakers.Clear();
        Assert.Equal("LinkDevicesRequired", SpeakerLinks.ValidationError(draft, [existing]));
        Assert.Null(SpeakerLinks.ValidationError(existing, [existing]));
    }

    [Fact]
    public async Task MultipleLinksAndActiveChoiceSurviveSettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Musync-link-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Link("Home", "desk", 42, 120);
            var second = Link("Night", "desk", 18, 350);
            var settings = new AppSettings { LinksInitialized = true, Links = [first, second], ActiveLinkId = second.Id };
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(settings);
            var restored = store.Load();
            Assert.Equal(second.Id, restored.ActiveLinkId);
            Assert.Equal(2, restored.Links.Count);
            Assert.Equal(42, restored.Links[0].Speakers["desk"].Preferences.Volume);
            Assert.Equal(18, restored.Links[1].Speakers["desk"].Preferences.Volume);
            Assert.Equal(350, restored.Links[1].Speakers["desk"].Preferences.DelayMs);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static SpeakerLink Link(string name, string id, double volume, int delay) => new()
    {
        Name = name,
        Speakers = new() { [id] = new() { Name = id, Preferences = new() { Selected = true, Volume = volume, DelayMs = delay } } }
    };
}
