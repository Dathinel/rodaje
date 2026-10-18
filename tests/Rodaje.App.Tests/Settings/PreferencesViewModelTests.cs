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

    [Fact]
    public void Linked_audio_selection_preserves_each_video_track()
    {
        var settings = new AppSettings { LinkedAudioVideoTracks = [1, 3, 7] };
        var model = new PreferencesViewModel(settings);

        Assert.Equal([1, 3, 7], model.LinkedAudioVideoTracks);
        model.SetLinkedAudioVideoTracks([2, 5]);
        Assert.Equal([2, 5], settings.LinkedAudioVideoTracks);
        Assert.Equal([2, 5], model.LinkedAudioVideoTracks);

        model.SetLinkedAudioVideoTracks(Enumerable.Range(1, NewProjectTracks.Max));
        Assert.Null(settings.LinkedAudioVideoTracks);
        Assert.Equal(Enumerable.Range(1, NewProjectTracks.Max), model.LinkedAudioVideoTracks);
    }

    [Fact]
    public void StillImageDuration_clamps_to_valid_range_and_updates_MediaRef()
    {
        double original = Rodaje.Core.Model.MediaRef.DefaultStillImageDuration.ToSeconds();
        try
        {
            var settings = new AppSettings();
            var model = new PreferencesViewModel(settings);

            model.StillImageDurationSeconds = 10;
            Assert.Equal(10, settings.StillImageDurationSeconds);
            Assert.Equal(10, Rodaje.Core.Model.MediaRef.DefaultStillImageDuration.ToSeconds());

            var photoMedia = new Rodaje.Core.Model.MediaRef { IsStillImage = true };
            Assert.Equal(10, photoMedia.InitialClipDuration.ToSeconds());

            model.StillImageDurationSeconds = 999;
            Assert.Equal(60, settings.StillImageDurationSeconds);

            model.StillImageDurationSeconds = -5;
            Assert.Equal(1, settings.StillImageDurationSeconds);

            model.StillCaptureFolder = @"D:\Capturas";
            Assert.Equal(@"D:\Capturas", settings.StillCaptureFolder);
        }
        finally
        {
            Rodaje.Core.Model.MediaRef.DefaultStillImageDuration = Rodaje.Core.Timing.Time.FromSeconds(original);
        }
    }
}
