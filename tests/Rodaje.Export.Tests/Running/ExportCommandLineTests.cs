using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.Export.Tests.Running;

/// <summary>«Copiar la línea de ffmpeg»: los mismos argumentos que la exportación, sin lanzar nada ni tocar el disco.</summary>
public sealed class ExportCommandLineTests
{
    private static readonly IReadOnlyList<EncoderSpec> Encoders =
        [.. ExportEncoderCatalog.All.Where(e => e.Vendor is not (EncoderVendor.Amd or EncoderVendor.Intel) && e.Id != "av1_nvenc")];

    private static ExportSettings H264 { get; } = new()
    {
        Video = new VideoExportSettings { Codec = ExportCodec.H264 },
        Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
    };

    private static ExportPlan Plan(ExportSettings settings, string output = @"C:\salidas\mi video.mp4") =>
        ExportPlan.Create(new ExportRequest(ExportPlanTests.Project(Time.FromSeconds(2)), "sec", output, settings), Encoders);

    [Fact]
    public void The_line_carries_the_pipe_input_the_encoder_its_arguments_and_the_output()
    {
        ExportPlan plan = Plan(H264);

        IReadOnlyList<string> args = ExportCommandLine.Arguments(plan);
        string text = ExportCommandLine.Text(plan);

        Assert.Contains("rawvideo", args);
        Assert.Contains("pipe:0", args);
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("aac_mf", args);
        Assert.Contains("+faststart", args);
        Assert.Equal(@"C:\salidas\mi video.mp4", args[^1]);
        Assert.Contains(Path.Combine(@"C:\salidas", ExportCommandLine.AudioPlaceholder), args);
        Assert.StartsWith("ffmpeg -hide_banner", text, StringComparison.Ordinal);
        Assert.EndsWith("\"C:\\salidas\\mi video.mp4\"", text, StringComparison.Ordinal);
        // Un argumento sin espacios va sin comillas; la salida, con ellas por el espacio.
        Assert.Contains(" -c:v:0 h264_nvenc ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_only_has_no_pipe_and_video_only_has_no_mix()
    {
        IReadOnlyList<string> audioOnly = ExportCommandLine.Arguments(Plan(new ExportSettings
        {
            Container = ExportContainer.M4a, Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        }, @"C:\salidas\voz.m4a"));
        IReadOnlyList<string> videoOnly = ExportCommandLine.Arguments(Plan(H264 with { Audio = null }));

        Assert.DoesNotContain("pipe:0", audioOnly);
        Assert.Contains("aac_mf", audioOnly);
        Assert.Contains("pipe:0", videoOnly);
        Assert.DoesNotContain(videoOnly, a => a.EndsWith(ExportCommandLine.AudioPlaceholder, StringComparison.Ordinal));
    }

    [Fact]
    public void Chapters_and_subtitles_are_left_out_because_they_live_in_temporary_files()
    {
        Project project = ExportPlanTests.Project(Time.FromSeconds(2));
        project.Sequences[0].Markers.Add(new Marker { Id = "m1", Name = "Uno", Time = Time.FromSeconds(1) });
        ExportPlan plan = ExportPlan.Create(new ExportRequest(project, "sec", @"C:\salidas\caps.mp4", H264 with { ChaptersFromMarkers = true }), Encoders);
        Assert.NotEmpty(plan.Metadata.Chapters);

        IReadOnlyList<string> args = ExportCommandLine.Arguments(plan);

        Assert.DoesNotContain("ffmetadata", args);
        Assert.Contains("-map_chapters", args);
        Assert.Equal("-1", args[args.ToList().IndexOf("-map_chapters") + 1]);
    }

    [Fact]
    public void An_image_sequence_does_not_create_its_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-linea-" + Guid.NewGuid().ToString("N"));
        ExportPlan plan = Plan(new ExportSettings
        {
            Container = ExportContainer.ImageSequence, Video = new VideoExportSettings { Codec = ExportCodec.Png },
        }, folder);

        IReadOnlyList<string> args = ExportCommandLine.Arguments(plan);

        Assert.False(Directory.Exists(folder));
        Assert.Contains(args, a => a.EndsWith("_%06d.png", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("con espacio", "\"con espacio\"")]
    [InlineData("di\"ce", "\"di\\\"ce\"")]
    [InlineData(@"C:\carpeta\", "\"C:\\carpeta\\\\\"")]
    [InlineData("barra\\ \\\"x", "\"barra\\ \\\\\\\"x\"")]
    [InlineData("a&b", "\"a&b\"")]
    [InlineData("tune=film,x", "\"tune=film,x\"")]
    public void Quoting_follows_the_rules_of_the_windows_console(string argument, string expected)
    {
        Assert.Equal(expected, ExportCommandLine.Quote(argument));
    }

    [Fact]
    public void Format_joins_the_executable_and_the_arguments_with_spaces()
    {
        Assert.Equal("ffmpeg -i \"mi entrada.mp4\" salida.mp4", ExportCommandLine.Format("ffmpeg", ["-i", "mi entrada.mp4", "salida.mp4"]));
        Assert.Equal("\"C:\\Program Files\\ffmpeg.exe\" -y", ExportCommandLine.Format(@"C:\Program Files\ffmpeg.exe", ["-y"]));
    }
}
