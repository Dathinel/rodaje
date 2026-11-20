using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Fcpxml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>R3 del repaso del 19-nov: los avisos de la exportación a FCPXML salían sin su código de tiempo.</summary>
public sealed class FcpxmlReportTimecodeTests
{
    [Fact]
    public void EveryClipIssueCarriesTheStartOfItsClip()
    {
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.FromSeconds(10), SourceOut = Time.FromSeconds(5), Color = "rojo", GroupId = "g", LinkId = "l" };
        var sequence = new Sequence { Id = "s", Name = "Prueba", FrameRate = Rational.Fps25,
            VideoTracks = [new Track { Id = "v", Name = "V1", Kind = TrackKind.Video, Clips = [clip] }] };
        var project = new Project { Id = "p", Sequences = [sequence], Media = [new MediaRef
        {
            Id = "m", Name = "Plano", Path = @"C:\Medios\Plano.mov", Duration = Time.FromSeconds(200),
            HasVideo = true, FrameRate = Rational.Fps25, Width = 1920, Height = 1080,
        }] };
        var report = new InterchangeReport();

        FcpxmlFidelity.Project(project, sequence, new InterchangeOptions(), report);

        InterchangeIssue[] ofTheClip = [.. report.Issues.Where(i => i.ClipId == "c")];
        Assert.True(ofTheClip.Length >= 3, "Faltan los avisos del color, el grupo y el enlace.");
        Assert.All(ofTheClip, i => Assert.Equal(Time.FromSeconds(10), i.Time));
    }
}
