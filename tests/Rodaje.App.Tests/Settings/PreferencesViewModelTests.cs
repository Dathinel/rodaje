using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services;
using Rodaje.App.Views.Preferences;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Tests.Settings;

public sealed class PreferencesViewModelTests
{
    [Fact]
    public void Preferences_read_and_write_persisted_settings()
    {
        var settings = new AppSettings();
        var model = new PreferencesViewModel(settings);

        model.VideoTracks = 5;
        model.AudioTracks = 3;
        model.GenerateProxies = false;
        model.GeneratedMediaFolder = "D:/renders";
        model.ProxyUsage = ProxyUsage.Never;
        model.PreviewQuality = PreviewQuality.Half;
        model.ShortcutPreset = ShortcutPreset.Classic;

        Assert.Equal(5, settings.DefaultVideoTracks);
        Assert.Equal(3, settings.DefaultAudioTracks);
        Assert.False(settings.AutoProxies);
        Assert.Equal("D:/renders", settings.GeneratedMediaFolder);
        Assert.Equal(ProxyUsage.Never, settings.ProxyUsage);
        Assert.Equal(PreviewQuality.Half, settings.PreviewQuality);
        Assert.Equal(ShortcutPreset.Classic, settings.ShortcutPreset);
    }

    [Fact]
    public void Track_counts_are_clamped_to_supported_range()
    {
        var model = new PreferencesViewModel(new AppSettings()) { VideoTracks = 99, AudioTracks = 0 };

        Assert.Equal(NewProjectTracks.Max, model.VideoTracks);
        Assert.Equal(NewProjectTracks.Min, model.AudioTracks);
    }
}
