using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Tarea 268: ffmpeg.exe no declara <c>longPathAware</c>, y la mezcla (<c>mezcla.wav</c>) vive en
/// <c>%TEMP%\rodaje-exportar-&lt;id&gt;.rodaje-tmp</c>: con un <c>TEMP</c> largo la ruta pasaba de MAX_PATH y la exportación
/// con audio fallaba después de codificar. Todo archivo que ffmpeg.exe abre por nombre (la mezcla, el original de una
/// copia directa, los capítulos, los subtítulos, la portada y la salida) llega con <c>\\?\</c> cuando es largo.
/// </summary>
/// <remarks>
/// Solo se arma la línea de órdenes, con rutas de Windows escritas a mano: la prueba es igual en Linux y en Windows y no
/// crea ningún archivo.
/// </remarks>
public sealed class FfmpegPipeArgumentsLongPathTests
{
    private const int MaxPath = 260;

    /// <summary>Una carpeta temporal de 206 caracteres, como la de un perfil con un nombre de usuario muy largo.</summary>
    private static readonly string LongTemp = @"C:\Users\" + new string('u', 150) + @"\AppData\Local\Temp\" + new string('t', 27);

    private static string Work => Path.Combine(LongTemp, $"rodaje-exportar-{new string('0', 32)}{ExportOutputGuard.TemporaryMarker}")
        .Replace('/', '\\');

    [Fact]
    public void The_mix_in_a_long_temporary_folder_reaches_ffmpeg_with_the_long_path_prefix()
    {
        string wave = Work + @"\mezcla.wav";
        var request = new ExportSinkRequest(@"C:\v\salida.m4a", ExportContainer.M4a, null, null, [],
            new ExportAudioFormat(48_000, 2, ExportSampleFormat.Pcm16), wave, SinkEncoders.For("aac"), ["-b:a", "192k"], 0);

        IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, @"C:\v\salida.m4a.rodaje-tmp", null);

        Assert.True(LongTemp.Length > 205, $"La prueba necesita un TEMP largo; tiene {LongTemp.Length}.");
        Assert.True(wave.Length >= MaxPath, $"La prueba necesita una mezcla que pase de MAX_PATH; tiene {wave.Length}.");
        Assert.Equal(@"\\?\" + wave, InputAfter(args, 0));
        Assert.DoesNotContain(wave, args);
    }

    [Fact]
    public void Every_file_ffmpeg_opens_by_name_gets_the_prefix_when_long()
    {
        string folder = @"C:\Proyectos\" + new string('p', 240);
        string output = folder + @"\salida.mkv";
        string temporary = output + ExportOutputGuard.TemporaryMarker;
        string chapters = temporary + ".ffmeta";
        string[] subtitles = [temporary + ".0.srt", temporary + ".1.srt"];
        string wave = Work + @"\mezcla.wav";
        string source = folder + @"\original.mp4";
        var request = new ExportSinkRequest(output, ExportContainer.Mkv, SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P),
            SinkEncoders.For("libvpx-vp9"), [], new ExportAudioFormat(48_000, 2, ExportSampleFormat.Pcm16), wave,
            SinkEncoders.For("libopus"), [], 30)
        {
            VideoCopy = new ExportStreamCopy(source, 0, 0, []),
        };

        IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, temporary, chapters, subtitles);

        Assert.Equal(@"\\?\" + source, InputAfter(args, 0));
        Assert.Equal(@"\\?\" + wave, InputAfter(args, 1));
        Assert.Equal(@"\\?\" + chapters, InputAfter(args, 2));
        Assert.Equal(@"\\?\" + subtitles[0], InputAfter(args, 3));
        Assert.Equal(@"\\?\" + subtitles[1], InputAfter(args, 4));
        Assert.Equal(@"\\?\" + temporary, args[^1]);
        foreach (string path in new[] { source, wave, chapters, subtitles[0], subtitles[1], temporary })
        {
            Assert.DoesNotContain(path, args);
        }
    }

    [Fact]
    public void Short_paths_reach_ffmpeg_unchanged()
    {
        string wave = Path.Combine(Path.GetTempPath(), "rodaje-exportar-1.rodaje-tmp", "mezcla.wav");
        string output = Path.Combine(Path.GetTempPath(), "salida.m4a");
        string temporary = output + ExportOutputGuard.TemporaryMarker;
        var request = new ExportSinkRequest(output, ExportContainer.M4a, null, null, [],
            new ExportAudioFormat(48_000, 2, ExportSampleFormat.Pcm16), wave, SinkEncoders.For("aac"), [], 0);

        IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, temporary, null);

        Assert.Equal(wave, InputAfter(args, 0));
        Assert.Equal(temporary, args[^1]);
        Assert.DoesNotContain(args, a => a.StartsWith(@"\\?\", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\\servidor\recurso\", @"\\?\UNC\servidor\recurso\")]
    [InlineData(@"\\?\C:\", @"\\?\C:\")]
    [InlineData("/tmp/", "/tmp/")]
    public void Network_prefixed_and_non_windows_paths(string start, string expectedStart)
    {
        string tail = new string('x', 300) + @"\mezcla.wav";
        string path = start + tail;
        if (start == "/tmp/")
        {
            tail = tail.Replace('\\', '/');
            path = start + tail;
        }

        Assert.Equal(expectedStart + tail, FfmpegPipeArguments.FilePath(path));
    }

    [Fact]
    public void The_threshold_leaves_room_below_max_path()
    {
        string below = @"C:\" + new string('a', FfmpegPipeArguments.LongPathThreshold - 4);
        string at = below + "b";

        Assert.Equal(FfmpegPipeArguments.LongPathThreshold - 1, below.Length);
        Assert.True(FfmpegPipeArguments.LongPathThreshold < MaxPath);
        Assert.Equal(below, FfmpegPipeArguments.FilePath(below));
        Assert.Equal(@"\\?\" + at, FfmpegPipeArguments.FilePath(at));
    }

    /// <summary>El valor de la <paramref name="index"/>-ésima <c>-i</c> que no es la tubería.</summary>
    private static string InputAfter(IReadOnlyList<string> args, int index)
    {
        List<string> inputs = [.. args.Select((a, i) => (a, i)).Where(p => p.a == "-i" && args[p.i + 1] != "pipe:0").Select(p => args[p.i + 1])];
        Assert.True(index < inputs.Count, $"Solo hay {inputs.Count} entradas: {string.Join(' ', args)}");
        return inputs[index];
    }
}
