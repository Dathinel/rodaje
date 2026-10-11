using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Export;

internal static class ExportTestData
{
    /// <summary>Secuencia 1920×1080 a 30 fps de 10 s: dos clips de 5 s y una región de 2 a 4 s.</summary>
    public static Project Project(int width = 1920, int height = 1080)
    {
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new Clip { Id = "a", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5) });
        track.Clips.Add(new Clip { Id = "b", MediaId = "m", Start = Time.FromSeconds(5), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5) });
        var sequence = new Sequence { Id = "sec", Name = "Principal", FrameRate = Rational.Fps30, Width = width, Height = height };
        sequence.VideoTracks.Add(track);
        sequence.Regions.Add(new Region { Id = "r", Name = "Intro", Start = Time.FromSeconds(2), End = Time.FromSeconds(4) });
        var project = new Project { Id = "p", Name = "Mi película: versión 1", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = @"C:\medios\clip.mp4" });
        return project;
    }
}
