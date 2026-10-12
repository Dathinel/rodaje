using Rodaje.App.Views.Library;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Library;

public class MediaDetailsTests
{
    [Fact]
    public void Resolucion_YFps_DeUnVideo()
    {
        var media = new MediaRef { Path = "a.mp4", HasVideo = true, FrameRate = Rational.Ntsc30, Width = 3840, Height = 2160 };

        Assert.Equal("3840×2160", MediaDetails.Resolution(media));
        Assert.Equal("29,97 fps", MediaDetails.FrameRate(media));
        Assert.Equal("23,976 fps", MediaDetails.FrameRate(Rational.Ntsc24));
        Assert.Equal("25 fps", MediaDetails.FrameRate(Rational.Fps25));
    }

    [Fact]
    public void FotoYAudio_NoTienenFps()
    {
        var photo = new MediaRef { Path = "a.png", HasVideo = true, FrameRate = Rational.Fps25, Width = 10, Height = 10 };
        var song = new MediaRef { Path = "a.mp3", HasAudio = true, SampleRate = 44100, AudioChannels = 1 };

        Assert.Equal("—", MediaDetails.FrameRate(photo));
        Assert.Equal("—", MediaDetails.FrameRate(song));
        Assert.Equal("—", MediaDetails.Resolution(song));
        Assert.Equal("44,1 kHz mono", MediaDetails.Audio(song));
        Assert.Equal("—", MediaDetails.Audio(photo));
    }

    [Fact]
    public void Codecs_ConNombresConocidos()
    {
        Assert.Equal("H.264 · AAC", MediaDetails.Codecs("h264", "aac", known: true));
        Assert.Equal("H.265", MediaDetails.Codecs("hevc", null, known: true));
        Assert.Equal("PCM", MediaDetails.Codecs(null, "pcm_s16le", known: true));
        Assert.Equal("CINEPAK", MediaDetails.Codecs("cinepak", "", known: true));
        Assert.Equal("—", MediaDetails.Codecs(null, null, known: true));
        Assert.Equal(MediaDetails.Pending, MediaDetails.Codecs(null, null, known: false));
    }

    [Fact]
    public void Resumen_JuntaLoQueHay()
    {
        var media = new MediaRef { Path = "a.mov", HasVideo = true, HasAudio = true, FrameRate = Rational.Fps60, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 };

        Assert.Equal("00:00:05:00 · 1920×1080 · 60 fps · 48 kHz estéreo · ProRes · PCM",
            MediaDetails.Summary(media, "00:00:05:00", "ProRes · PCM"));
        Assert.Equal("00:00:05:00 · 1920×1080 · 60 fps · 48 kHz estéreo", MediaDetails.Summary(media, "00:00:05:00", MediaDetails.Pending));
    }
}
