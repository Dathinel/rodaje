using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Visor.Tests;

/// <summary>Medios pequeños generados una vez por clase: dos videos, un audio, un PNG y un JPG.</summary>
public sealed class VisorMediaFixture : IDisposable
{
    public VisorMediaFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-visor", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        if (FfmpegTool.Default is not { } ffmpeg || ffmpeg.FindVideoEncoder(VideoCodec.H264) is not { } h264)
        {
            return;
        }

        string[] quiet = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"];
        foreach ((string name, string size) in (ReadOnlySpan<(string, string)>)[("toma uno", "320x240"), ("toma dos", "320x240")])
        {
            ffmpeg.Run([.. quiet, "-f", "lavfi", "-i", $"testsrc2=s={size}:r=25:d=2", "-c:v", h264, "-pix_fmt", "yuv420p", Combine(name + ".mp4")]);
        }

        ffmpeg.Run([.. quiet, "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "aac", Combine("solo audio.m4a")]);
        ffmpeg.Run([.. quiet, "-f", "lavfi", "-i", "testsrc2=s=64x48", "-frames:v", "1", Combine("foto.png")]);
        ffmpeg.Run([.. quiet, "-f", "lavfi", "-i", "testsrc2=s=64x48", "-frames:v", "1", Combine("foto.jpg")]);
    }

    public string Directory { get; }

    public string Combine(string name) => Path.Combine(Directory, name);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class VisorProjectTests(VisorMediaFixture media) : IClassFixture<VisorMediaFixture>
{
    [FfmpegFact(VideoCodec.H264)]
    public void Still_images_are_skipped_with_a_reason_and_the_videos_still_play()
    {
        VisorProject project = VisorProject.BuildDemo(
            [media.Combine("toma uno.mp4"), media.Combine("foto.png"), media.Combine("toma dos.mp4"), media.Combine("foto.jpg")]);

        Assert.Equal(["foto.png", "foto.jpg"], project.Skipped.Select(s => Path.GetFileName(s.Path)));
        Assert.All(project.Skipped, s => Assert.Contains("imagen fija", s.Reason, StringComparison.Ordinal));
        Assert.Equal(2, project.Sequence.VideoTracks.Count);
        Assert.Equal(2, project.Project.Media.Count);
        Assert.Contains("foto.png: es una imagen fija", VisorProject.DescribeSkipped(project.Skipped), StringComparison.Ordinal);
    }

    [FfmpegFact(VideoCodec.H264)]
    public void Only_still_images_are_refused_with_the_list_of_reasons()
    {
        var ex = Assert.Throws<ArgumentException>(() => VisorProject.BuildDemo([media.Combine("foto.png"), media.Combine("foto.jpg")]));

        Assert.Contains("foto.png", ex.Message, StringComparison.Ordinal);
        Assert.Contains("foto.jpg", ex.Message, StringComparison.Ordinal);
    }

    [FfmpegFact(VideoCodec.H264)]
    public void A_demo_never_takes_the_name_of_an_existing_project_and_saving_does_not_overwrite_it()
    {
        string folder = Path.Combine(media.Directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string video = Path.Combine(folder, "toma uno.mp4");
        File.Copy(media.Combine("toma uno.mp4"), video);
        string existing = Path.Combine(folder, "toma uno.rodaje");
        File.WriteAllText(existing, "otro proyecto");

        VisorProject project = VisorProject.BuildDemo([video]);

        Assert.True(project.IsDemo);
        Assert.False(project.IsSaved);
        Assert.Equal(Path.Combine(folder, "toma uno (2).rodaje"), project.SavePath);

        File.WriteAllText(project.SavePath, "creado mientras tanto");
        project.Save();

        Assert.True(project.IsSaved);
        Assert.Equal(Path.Combine(folder, "toma uno (3).rodaje"), project.SavePath);
        Assert.Equal("otro proyecto", File.ReadAllText(existing));
        Assert.Equal("creado mientras tanto", File.ReadAllText(Path.Combine(folder, "toma uno (2).rodaje")));
        Assert.Single(VisorProject.Load(project.SavePath).Project.Media);

        project.Save();
        Assert.Equal(Path.Combine(folder, "toma uno (3).rodaje"), project.SavePath);
    }

    [Fact]
    public void A_loaded_project_counts_as_saved_and_keeps_its_path()
    {
        string folder = Path.Combine(media.Directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "proyecto.rodaje");
        Project project = ProjectFactory.CreateDefault("proyecto", GuidIdGenerator.Instance, Rational.Fps25);
        Rodaje.Core.Serialization.ProjectSerializer.Save(project, path);

        VisorProject loaded = VisorProject.Load(path);

        Assert.False(loaded.IsDemo);
        Assert.True(loaded.IsSaved);
        Assert.Equal(path, loaded.SavePath);
        Assert.Empty(loaded.Skipped);
    }

    [FfmpegFact(VideoCodec.H264)]
    public void An_audio_only_demo_skips_the_layer_edits_of_the_automated_run()
    {
        VisorProject audio = VisorProject.BuildDemo([media.Combine("solo audio.m4a")]);
        VisorProject video = VisorProject.BuildDemo([media.Combine("toma uno.mp4"), media.Combine("toma dos.mp4")]);
        var run = new AutomatedRun(TimeSpan.FromSeconds(10), logPath: null);

        IReadOnlyList<string> audioSteps = run.StepsFor(audio.Sequence);
        IReadOnlyList<string> videoSteps = run.StepsFor(video.Sequence);

        Assert.False(AutomatedRun.HasVideoClips(audio.Sequence));
        Assert.True(AutomatedRun.HasVideoClips(video.Sequence));
        Assert.Equal(11, videoSteps.Count);
        Assert.Equal(["seek al 50 %", "seek al 10 %", "pausa y pasos de cuadro", "reproducir", "Shift+→ (+5 s)"], audioSteps);
    }

    [Fact]
    public void Picture_in_picture_uses_the_rotated_display_size()
    {
        Sequence sequence = ProjectFactory.CreateDefault("pip", GuidIdGenerator.Instance, Rational.Fps30).Sequences[0];
        sequence.Width = 1920;
        sequence.Height = 1080;
        VideoStreamInfo portrait = Stream(width: 1920, height: 1080, rotation: 90, displayWidth: 1080, displayHeight: 1920);

        EffectInstance effect = VisorProject.PictureInPicture(sequence, portrait, corner: 0);

        // Vertical: encaja por el alto (1080 / 1920), así que la caja mide 212,6 × 378 y queda en la esquina de arriba a la derecha.
        double fit = 1080.0 / 1920;
        double width = 1080 * fit * VisorProject.PictureInPictureScale;
        double height = 1920 * fit * VisorProject.PictureInPictureScale;
        double margin = 1080 * 0.04;
        Assert.Equal(Math.Round(960 - (width / 2) - margin), effect.Parameters[P.X].Value);
        Assert.Equal(Math.Round(-(540 - (height / 2) - margin)), effect.Parameters[P.Y].Value);
        Assert.Equal(VisorProject.PictureInPictureScale, effect.Parameters[P.Scale].Value);
    }

    private static VideoStreamInfo Stream(int width, int height, int rotation, int displayWidth, int displayHeight) => new()
    {
        StreamIndex = 0,
        Codec = "h264",
        PixelFormat = "yuv420p",
        BitDepth = 8,
        Width = width,
        Height = height,
        SampleAspectRatio = new Rational(1, 1),
        Rotation = rotation,
        DisplayWidth = displayWidth,
        DisplayHeight = displayHeight,
        FrameRate = Rational.Fps30,
        IsVariableFrameRate = false,
        FrameCount = 60,
        Duration = Time.FromSeconds(2),
        StartTime = Time.Zero,
    };
}
