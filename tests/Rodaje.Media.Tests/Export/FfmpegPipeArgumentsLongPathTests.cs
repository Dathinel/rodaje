using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Tarea 268: ffmpeg.exe no declara <c>longPathAware</c>, y la mezcla (<c>mezcla.wav</c>) vive en
/// <c>%TEMP%\rodaje-exportar-&lt;id&gt;.rodaje-tmp</c>: con un <c>TEMP</c> largo la ruta pasaba de MAX_PATH y la exportación
/// con audio fallaba después de codificar. Todo archivo que ffmpeg.exe abre por nombre (la mezcla, el original de una
/// copia directa, los capítulos, los subtítulos, la portada y la salida) llega con <c>\\?\</c> cuando es largo.
/// </summary>
/// <remarks>
/// Solo se arma la línea de órdenes, con rutas de Windows escritas a mano: la prueba es igual en Linux y en Windows. Solo
/// la portada y la secuencia de imágenes crean algo (la portada solo entra si existe; la secuencia crea su carpeta).
/// </remarks>
public sealed class FfmpegPipeArgumentsLongPathTests
{
    private const int MaxPath = 260;

    /// <summary>Una carpeta temporal de 206 caracteres, como la de un perfil con un nombre de usuario muy largo.</summary>
    private static readonly string LongTemp = @"C:\Users\" + new string('u', 150) + @"\AppData\Local\Temp\" + new string('t', 27);

    private static string Work => Path.Combine(LongTemp, $"rodaje-exportar-{new string('0', 32)}{ExportOutputGuard.TemporaryMarker}")
        .Replace('/', '\\');

    [Fact]
    public void The_loudness_measurement_opens_the_mix_with_the_long_path_prefix()
    {
        string wave = Work + @"\mezcla.wav";

        IReadOnlyList<string> args = FfmpegLoudness.MeasureArguments(wave, -14, -1);

        Assert.True(wave.Length >= MaxPath, $"La prueba necesita una mezcla que pase de MAX_PATH; tiene {wave.Length}.");
        Assert.Equal(@"\\?\" + wave, InputAfter(args, 0));
        Assert.Equal(@"C:\v\mezcla.wav", InputAfter(FfmpegLoudness.MeasureArguments(@"C:\v\mezcla.wav", -14, -1), 0));
        Assert.Contains("loudnorm=I=-14.0:TP=-1.0:LRA=11.0:print_format=json", args);
    }

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

    /// <summary>
    /// Tarea 309: la portada solo entra en la línea de órdenes si el archivo existe, así que aquí se crea de verdad, con
    /// una ruta de 252 caracteres.
    /// </summary>
    [Fact]
    public void A_real_cover_in_a_long_folder_reaches_ffmpeg_with_the_long_path_prefix()
    {
        string root = ScratchRoot();
        string cover = LongPath(root, 252, ".png");
        try
        {
            CreateParent(cover);
            File.WriteAllBytes(cover, [0x89, 0x50, 0x4E, 0x47]);
            var request = new ExportSinkRequest(@"C:\v\salida.mp4", ExportContainer.Mp4,
                SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P), SinkEncoders.For("libsvtav1"), [], null, null, null, [], 30)
            {
                Metadata = new ExportMetadata { ThumbnailPath = cover },
            };

            IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, @"C:\v\salida.mp4.rodaje-tmp", null);

            Assert.Equal(252, cover.Length);
            Assert.Equal(@"\\?\" + cover, InputAfter(args, 0));
            Assert.DoesNotContain(cover, args);
        }
        finally
        {
            DeleteScratch(root, cover);
        }
    }

    /// <summary>
    /// Tarea 309: la secuencia de imágenes no nombra un archivo sino un patrón (<c>nombre_%06d.png</c>) dentro de la
    /// carpeta temporal; con una carpeta larga el patrón también llega con <c>\\?\</c>.
    /// </summary>
    [Fact]
    public void An_image_sequence_in_a_long_folder_gets_the_prefix_on_its_pattern()
    {
        string root = ScratchRoot();
        string folder = LongPath(root, 240, "");
        string temporary = folder + ExportOutputGuard.TemporaryMarker;
        try
        {
            var request = new ExportSinkRequest(Path.Combine(folder, "salida"), ExportContainer.ImageSequence,
                SinkRuns.Format(64, 36, ExportPixelFormat.Rgb24), SinkEncoders.For("png"), [], null, null, null, [], 5);

            IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, temporary, null);

            string pattern = Path.Combine(temporary, "salida_%06d.png");
            Assert.True(pattern.Length >= MaxPath, $"La prueba necesita un patrón que pase de MAX_PATH; tiene {pattern.Length}.");
            Assert.Equal(@"\\?\" + pattern, args[^1]);
            Assert.DoesNotContain(pattern, args);
        }
        finally
        {
            DeleteScratch(root, temporary);
        }
    }

    /// <summary>
    /// Tarea 309: con <c>\\?\</c> Windows ya no resuelve «..» ni «/», así que la ruta se normaliza antes de ponerle el
    /// prefijo. En Linux <see cref="FfmpegPipeArguments.FilePath"/> no normaliza (una ruta como <c>C:\…</c> no es absoluta).
    /// </summary>
    [WindowsOnlyFact("la normalización con Path.GetFullPath solo se hace con rutas de Windows absolutas.")]
    public void A_long_path_with_dot_dot_and_forward_slashes_is_normalized_before_the_prefix()
    {
        string folder = @"C:\Proyectos\" + new string('p', 240);
        string messy = @"C:/Proyectos/" + new string('p', 240) + @"/borrar\..\mezcla.wav";

        Assert.Equal(@"\\?\" + folder + @"\mezcla.wav", FfmpegPipeArguments.FilePath(messy));
    }

    /// <summary>
    /// Tarea 309: el umbral con valores literales (no con <see cref="FfmpegPipeArguments.LongPathThreshold"/>), para que
    /// subirlo hacia MAX_PATH haga caer la prueba: desde 248 caracteres la ruta va con prefijo; con 247, sin él.
    /// </summary>
    [Theory]
    [InlineData(247, false)]
    [InlineData(248, true)]
    [InlineData(250, true)]
    [InlineData(258, true)]
    public void The_threshold_is_248_characters(int length, bool prefixed)
    {
        string path = @"C:\" + new string('a', length - 3);

        Assert.Equal(length, path.Length);
        Assert.Equal(prefixed ? @"\\?\" + path : path, FfmpegPipeArguments.FilePath(path));
    }

    /// <summary>
    /// Carpeta propia de una prueba que crea archivos. En Windows, una carpeta de verdad en <c>%TEMP%</c>. En Linux, un
    /// nombre relativo que empieza por <c>C:\</c> (en Linux «\» no separa carpetas, así que todo es un solo nombre en la
    /// carpeta actual): así <see cref="File.Exists(string?)"/> lo encuentra y <see cref="FfmpegPipeArguments.FilePath"/> lo
    /// trata como una ruta de Windows.
    /// </summary>
    private static string ScratchRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Path.GetTempPath(), "rodaje-ruta-larga-" + Guid.NewGuid().ToString("N"))
        : @"C:\rodaje-ruta-larga-" + Guid.NewGuid().ToString("N");

    /// <summary>Una ruta dentro de <paramref name="root"/> de exactamente <paramref name="length"/> caracteres.</summary>
    private static string LongPath(string root, int length, string extension)
    {
        string prefix = root + @"\";
        return prefix + new string('r', length - prefix.Length - extension.Length) + extension;
    }

    private static void CreateParent(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
    }

    private static void DeleteScratch(string root, string created)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            return;
        }

        if (Directory.Exists(created))
        {
            Directory.Delete(created, recursive: true);
        }
        else if (File.Exists(created))
        {
            File.Delete(created);
        }
    }

    /// <summary>Prueba que solo tiene sentido en Windows; fuera de él se informa como omitida, con la causa.</summary>
    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute(string because)
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = $"Solo Windows: {because}";
            }
        }
    }

    /// <summary>El valor de la <paramref name="index"/>-ésima <c>-i</c> que no es la tubería.</summary>
    private static string InputAfter(IReadOnlyList<string> args, int index)
    {
        List<string> inputs = [.. args.Select((a, i) => (a, i)).Where(p => p.a == "-i" && args[p.i + 1] != "pipe:0").Select(p => args[p.i + 1])];
        Assert.True(index < inputs.Count, $"Solo hay {inputs.Count} entradas: {string.Join(' ', args)}");
        return inputs[index];
    }
}
