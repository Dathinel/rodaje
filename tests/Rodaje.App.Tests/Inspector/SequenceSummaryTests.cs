using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public class SequenceSummaryTests
{
    [Fact]
    public void SinSeleccion_MuestraFormatoYContenidoDeLaSecuencia()
    {
        var sequence = new Sequence { Id = "s", Name = "Principal", Width = 1080, Height = 1920, FrameRate = Rational.Fps30 };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video });
        sequence.VideoTracks.Add(new Track { Id = "v2", Kind = TrackKind.Video });
        sequence.AudioTracks.Add(new Track { Id = "a1", Kind = TrackKind.Audio });
        sequence.VideoTracks[0].Clips.Add(new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(12) });

        Dictionary<string, string> rows = SequenceSummary.Describe(sequence).ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal("9:16", rows["Formato"]);
        Assert.Equal("1080×1920", rows["Resolución"]);
        Assert.Equal("30", rows["Cuadros por segundo"]);
        Assert.Equal("1", rows["Clips"]);
        Assert.Equal("2 de video y 1 de audio", rows["Pistas"]);
        Assert.Equal("0:12.000", rows["Duración"]);
    }
}
