using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>Carpeta temporal con los medios raros de <see cref="RealWorldMediaTests"/>, generados una vez por clase.</summary>
public sealed class RealWorldMediaFixture : IDisposable
{
    private readonly Dictionary<string, string> _made = [];

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("rodaje-medios raros ñ-").FullName;

    /// <summary>Genera (una vez) <paramref name="name"/> con ffmpeg; <c>{base}</c> en los argumentos es un MP4 corriente.</summary>
    public string Make(string name, string arguments)
    {
        lock (_made)
        {
            if (_made.TryGetValue(name, out string? done))
            {
                return done;
            }

            string path = Path.Combine(Directory, name);
            if (arguments.Contains("{base}", StringComparison.Ordinal))
            {
                arguments = arguments.Replace("{base}", "\"" + Make("base.mp4", RealWorldMediaTests.Picture + " " + RealWorldMediaTests.Tone + " -c:v mpeg4 -c:a aac -shortest") + "\"", StringComparison.Ordinal);
            }

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            args.AddRange(Split(arguments));
            args.Add(path);
            FfmpegTool.Default!.Run(args);
            _made[name] = path;
            return path;
        }
    }

    // Los argumentos van separados por espacios; un tramo entre comillas dobles es uno solo (rutas y filtros).
    private static IEnumerable<string> Split(string text)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

/// <summary>
/// Medios de la vida real que la app debe abrir sin colgarse ni caer: de celular (rotación, vertical, cuadro perdido), de
/// cámara (MPEG-TS y AVCHD con cuadros B, entrelazado, píxel no cuadrado, TC de 10 h), recortados sin recodificar (lista
/// de edición), audio raro (5.1, 7.1, mono 8 kHz, 96 kHz 24 bits, float, Opus, Vorbis, varias pistas), con subtítulos,
/// imágenes y archivos rotos. Cada uno pasa por lo que hace la app al importarlo: <see cref="MediaSupport.Classify"/>,
/// miniaturas (<see cref="ThumbnailDecoder"/>), cuadros (<see cref="VideoDecoder"/> por CPU) y audio
/// (<see cref="AudioDecoder"/>). Solo usa codificadores propios de FFmpeg, que traen todas las compilaciones.
/// </summary>
public sealed class RealWorldMediaTests(RealWorldMediaFixture fixture, ITestOutputHelper output) : IClassFixture<RealWorldMediaFixture>
{
    internal const string Picture = "-f lavfi -i testsrc2=s=320x240:r=30:d=3";
    internal const string Tone = "-f lavfi -i sine=f=440:r=48000:d=3";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Nombre, argumentos de ffmpeg, tamaño visible esperado y si trae audio.</summary>
    public static TheoryData<string, string, int, int, bool> Usable => new()
    {
        // Celular: la matriz de rotación gira lo visible; ffmpeg la escribe en sentido antihorario.
        { "rot90.mp4", "-display_rotation:v:0 90 -i {base} -c copy", 240, 320, true },
        { "rot180.mp4", "-display_rotation:v:0 180 -i {base} -c copy", 320, 240, true },
        { "rot270.mp4", "-display_rotation:v:0 270 -i {base} -c copy", 240, 320, true },
        { "vertical 1080x2400.mp4", "-f lavfi -i testsrc2=s=1080x2400:r=30:d=1 " + Tone + " -c:v mpeg4 -q:v 10 -c:a aac -shortest", 1080, 2400, true },
        { "cuadro perdido.mp4", Picture + " " + Tone + " -vf select=not(eq(n\\,45)) -fps_mode passthrough -c:v mpeg4 -c:a aac", 320, 240, true },
        { "vfr.mkv", Picture + " " + Tone + " -vf setpts=(N/30+0.013*sin(N*1.7)+if(gt(N\\,40)\\,0.4\\,0))/TB -fps_mode vfr -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "cuadro repetido.mkv", "-f lavfi -i testsrc2=s=320x240:r=30:d=3 " + Tone + " -vf settb=1/1000,setpts=(floor(N/2)*2/30+mod(N\\,2)*0.002)/TB -fps_mode passthrough -enc_time_base:v filter -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "5994.mp4", "-f lavfi -i testsrc2=s=320x240:r=60000/1001:d=2 " + Tone + " -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "2997 drop.mov", "-f lavfi -i testsrc2=s=320x240:r=30000/1001:d=2 " + Tone + " -timecode 00:59:59;28 -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "240.mp4", "-f lavfi -i testsrc2=s=320x240:r=240:d=1 -c:v mpeg4", 320, 240, false },
        // Cámara: MPEG-TS y AVCHD (.m2ts) con cuadros B y un solo keyframe; entrelazado con píxel no cuadrado.
        { "camara.ts", Picture + " " + Tone + " -c:v mpeg2video -bf 2 -g 300 -c:a mp2 -shortest", 320, 240, true },
        { "avchd.m2ts", Picture + " " + Tone + " -c:v mpeg2video -bf 2 -g 300 -c:a ac3 -shortest -f mpegts -mpegts_m2ts_mode 1", 320, 240, true },
        { "10 horas.ts", Picture + " " + Tone + " -c:v mpeg2video -bf 2 -g 300 -c:a mp2 -shortest -output_ts_offset 36000", 320, 240, true },
        { "10 horas.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest -output_ts_offset 36000", 320, 240, true },
        { "dvd entrelazado.mpg", "-f lavfi -i testsrc2=s=720x480:r=30000/1001:d=2,setsar=10/11 " + Tone + " -c:v mpeg2video -flags +ildct+ilme -b:v 4M -c:a mp2 -shortest", 640, 480, true },
        { "1080i.ts", "-f lavfi -i testsrc2=s=1920x1080:r=30000/1001:d=1 -c:v mpeg2video -flags +ildct+ilme -b:v 8M", 1920, 1080, false },
        { "sar 4-3.mov", "-f lavfi -i testsrc2=s=720x480:r=30000/1001:d=2,setsar=4/3 -c:v mpeg4", 960, 480, false },
        // Tamaños y formatos de píxel.
        { "impar 1281x721.mkv", "-f lavfi -i testsrc2=s=1282x722:r=30:d=1,format=rgb24,scale=1281:721,setsar=1,format=yuv444p10le -c:v ffv1", 1281, 721, false },
        { "16x16.mkv", "-f lavfi -i testsrc2=s=16x16:r=30:d=1 -c:v ffv1", 16, 16, false },
        { "422 10 bits.mov", Picture + " -c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le", 320, 240, false },
        { "hlg.mkv", Picture + " -c:v ffv1 -pix_fmt yuv420p10le -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc", 320, 240, false },
        // Recortado sin recodificar: la lista de edición oculta los primeros 0,7 s.
        { "lista de edicion.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest -output_ts_offset -0.7 -avoid_negative_ts disabled", 320, 240, true },
        // Audio.
        { "51.mp4", Picture + " " + Tone + " -filter_complex [1]pan=5.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0[a] -map 0 -map [a] -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "71.mkv", Picture + " " + Tone + " -filter_complex [1]pan=7.1|c0=c0|c1=c0|c2=c0|c3=c0|c4=c0|c5=c0|c6=c0|c7=c0[a] -map 0 -map [a] -c:v mpeg4 -c:a flac -shortest", 320, 240, true },
        { "mono 8k.mp4", Picture + " -f lavfi -i sine=r=8000:d=3 -c:v mpeg4 -c:a aac -ac 1 -shortest", 320, 240, true },
        { "22050.mov", Picture + " -f lavfi -i sine=r=22050:d=3 -c:v mpeg4 -c:a pcm_s16le -shortest", 320, 240, true },
        { "96k 24 bits.mov", Picture + " -f lavfi -i sine=r=96000:d=3 -c:v mpeg4 -c:a pcm_s24le -shortest", 320, 240, true },
        { "float.mov", Picture + " " + Tone + " -c:v mpeg4 -c:a pcm_f32le -shortest", 320, 240, true },
        { "opus.mkv", Picture + " " + Tone + " -c:v mpeg4 -c:a opus -strict -2 -ac 2 -shortest", 320, 240, true },
        { "vorbis.mkv", Picture + " " + Tone + " -c:v mpeg4 -c:a vorbis -strict -2 -ac 2 -shortest", 320, 240, true },
        { "dos pistas.mov", Picture + " " + Tone + " -f lavfi -i sine=f=880:r=44100:d=3 -map 0 -map 1 -map 2 -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
        { "subtitulos primero.mkv", "-f lavfi -i testsrc2=s=320x240:r=30:d=3 " + Tone + " -f srt -i \"{srt}\" -map 2 -map 1 -map 0 -c:v mpeg4 -c:a aac -c:s srt -shortest", 320, 240, true },
        { "sin audio.avi", Picture + " -c:v mjpeg", 320, 240, false },
        { "solo audio.wav", Tone + " -c:a pcm_s16le", 0, 0, true },
        { "animado.gif", "-f lavfi -i testsrc2=s=160x120:r=10:d=2", 160, 120, false },
        { "nombre con ñ, tildes á é y 🎬.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest", 320, 240, true },
    };

    [FfmpegLibraryTheory]
    [MemberData(nameof(Usable))]
    public async Task Odd_media_imports_and_decodes_like_the_app_does(string name, string arguments, int displayWidth, int displayHeight, bool hasAudio)
    {
        string path = fixture.Make(name, arguments.Replace("{srt}", Subtitles(), StringComparison.Ordinal));
        string? problem = null;
        MediaInfo? info = null;
        await Bounded(() => problem = MediaSupport.Classify(path, out info));
        Assert.True(problem is null, $"{name}: {problem}");
        output.WriteLine($"{name}: {info!.Duration} {info.Video} {info.Audio}");
        Assert.InRange(info.Duration.ToSeconds(), 0.5, 3.5);
        Assert.Equal(hasAudio, info.HasAudio);
        if (info.Video is { } video)
        {
            Assert.Equal((displayWidth, displayHeight), (video.DisplayWidth, video.DisplayHeight));
            // Los cuadros repetidos a 2 ms daban 500 fps y 1493 cuadros en 3 s.
            Assert.InRange(video.FrameCount, 1, (long)(info.Duration.ToSeconds() * 250));
            if (info.Audio is { } sound && !video.IsVariableFrameRate)
            {
                // Un clip con más cuadros que su audio (la lista de edición contaba los ocultos) acababa en un cuadro congelado.
                double end = (video.StartTime + video.Duration - (sound.StartTime + sound.Duration)).ToSeconds();
                Assert.True(Math.Abs(end) < 0.15, string.Create(CultureInfo.InvariantCulture, $"El video acaba {end:0.000} s después del audio."));
            }

            await Bounded(() => DecodesEverywhere(path, info));
        }

        if (hasAudio)
        {
            await Bounded(() =>
            {
                using AudioDecoder audio = AudioDecoder.Open(path);
                var buffer = new float[8192 * AudioDecoder.OutputChannels];
                long frames = 0;
                int read;
                while ((read = audio.Read(buffer)) > 0)
                {
                    frames += read;
                }

                double seconds = (double)frames / AudioDecoder.OutputSampleRate;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"audio: {seconds:0.000} s de {audio.Duration.ToSeconds():0.000} s, {audio.DecodeErrors} errores"));
                Assert.Equal(0, audio.DecodeErrors);
                // MPEG-TS no declara la duración del audio y FFmpeg la estima: puede faltar algún paquete AC-3 (32 ms).
                Assert.InRange(seconds, audio.Duration.ToSeconds() - 0.1, audio.Duration.ToSeconds() + 0.1);
            });
        }
    }

    /// <summary>Lo que la app no puede usar se rechaza al importar con un motivo en español, sin excepciones.</summary>
    [FfmpegLibraryTheory]
    [InlineData("vacio.mp4", "no se pudo leer")]
    [InlineData("moov al final cortado.mp4", "no se pudo leer")]
    [InlineData("un cuadro.mp4", "un cuadro")]
    public async Task Broken_media_is_refused_with_a_reason(string name, string reason)
    {
        string path = Path.Combine(fixture.Directory, name);
        if (name.StartsWith("vacio", StringComparison.Ordinal))
        {
            await File.WriteAllBytesAsync(path, []);
        }
        else if (name.StartsWith("moov", StringComparison.Ordinal))
        {
            byte[] whole = await File.ReadAllBytesAsync(fixture.Make("moov al final.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest"));
            await File.WriteAllBytesAsync(path, whole[..(whole.Length / 2)]);
        }
        else
        {
            path = fixture.Make(name, Picture + " -frames:v 1 -c:v mpeg4");
        }

        string? problem = null;
        await Bounded(() => problem = MediaSupport.Classify(path, out _));
        output.WriteLine($"{name}: {problem}");
        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    /// <summary>Una ruta de más de 260 caracteres (carpetas anidadas de proyectos) se lee, se decodifica y da miniatura.</summary>
    [FfmpegLibraryFact]
    public async Task A_path_longer_than_260_characters_works()
    {
        string folder = fixture.Directory;
        while (folder.Length < 280)
        {
            folder = Path.Combine(folder, "carpeta de proyecto con nombre largo ñ");
        }

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "clip final versión 3 🎬.mp4");
        File.Copy(fixture.Make("base.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest"), path);
        Assert.True(path.Length > 260);
        string? problem = "sin leer";
        MediaInfo? info = null;
        await Bounded(() => problem = MediaSupport.Classify(path, out info));
        Assert.Null(problem);
        await Bounded(() => DecodesEverywhere(path, info!));
    }

    /// <summary>
    /// Un medio borrado o bloqueado por otro programa después de importarlo: la lectura falla con una excepción que la app
    /// ya atiende (archivo que falta o <see cref="MediaException"/>), nunca con un cuelgue o un acceso a memoria inválido.
    /// </summary>
    [FfmpegLibraryFact]
    public async Task Deleted_or_locked_media_fails_with_a_reason()
    {
        string path = Path.Combine(fixture.Directory, "se va a borrar.mp4");
        File.Copy(fixture.Make("base.mp4", Picture + " " + Tone + " -c:v mpeg4 -c:a aac -shortest"), path, overwrite: true);
        Assert.Null(MediaSupport.Classify(path, out _));
        if (OperatingSystem.IsWindows())
        {
            // Solo Windows bloquea de verdad un archivo abierto sin compartir.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string? locked = null;
                await Bounded(() => locked = MediaSupport.Classify(path, out _));
                output.WriteLine($"bloqueado: {locked}");
                Assert.Contains("otro programa", locked, StringComparison.Ordinal);
                await Bounded(() => Assert.ThrowsAny<MediaException>(() => VideoDecoder.Open(path).Dispose()));
            }
        }

        File.Delete(path);
        string? deleted = null;
        await Bounded(() => deleted = MediaSupport.Classify(path, out _));
        output.WriteLine($"borrado: {deleted}");
        Assert.Equal("el archivo ya no existe", deleted);
        await Bounded(() =>
        {
            Assert.Throws<FileNotFoundException>(() => VideoDecoder.Open(path).Dispose());
            Assert.Throws<FileNotFoundException>(() => ThumbnailDecoder.Open(path).Dispose());
            Assert.Throws<FileNotFoundException>(() => AudioDecoder.Open(path).Dispose());
        });
    }

    /// <summary>
    /// El cuadro con el destello y el pitido empiezan juntos en 1 s: en el medio leído por la app no se separan más de un
    /// cuadro, también donde el audio empieza antes que el video (MPEG-TS), a 10 h o tras una lista de edición.
    /// </summary>
    [FfmpegLibraryTheory]
    [InlineData("sync.mp4", "-c:v mpeg4 -c:a aac")]
    [InlineData("sync.ts", "-c:v mpeg2video -bf 2 -g 300 -c:a mp2")]
    [InlineData("sync.m2ts", "-c:v mpeg2video -bf 2 -g 300 -c:a ac3 -f mpegts -mpegts_m2ts_mode 1")]
    [InlineData("sync 10h.ts", "-c:v mpeg2video -bf 2 -g 300 -c:a mp2 -output_ts_offset 36000")]
    [InlineData("sync 96k.mov", "-c:v mpeg4 -c:a pcm_s24le -ar 96000")]
    [InlineData("sync opus.mkv", "-c:v mpeg4 -c:a opus -strict -2 -ac 2")]
    [InlineData("sync recortado.mp4", "-c:v mpeg4 -c:a aac -output_ts_offset -0.5 -avoid_negative_ts disabled")]
    public async Task Flash_and_beep_stay_within_a_frame(string name, string codecs)
    {
        string path = fixture.Make(name, FlashAndBeep + " " + codecs + " -shortest");
        await Bounded(() =>
        {
            using VideoDecoder video = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
            Time frame = Time.FromFrames(1, video.FrameRate);
            long flash = -1;
            for (long i = 0; i < video.FrameCount && flash < 0; i++)
            {
                using var decoded = video.GetFrame(i);
                Assert.True(decoded is not null, $"{name}: el cuadro {i} no salió.");
                if (Luma(decoded) > 128)
                {
                    flash = i;
                }
            }

            using AudioDecoder audio = AudioDecoder.Open(path);
            Assert.Equal(video.Stream.StartTime, audio.Origin);
            var buffer = new float[4096 * AudioDecoder.OutputChannels];
            long position = 0;
            long beep = -1;
            int read;
            while (beep < 0 && (read = audio.Read(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (Math.Abs(buffer[i * AudioDecoder.OutputChannels]) > 0.25f)
                    {
                        beep = position + i;
                        break;
                    }
                }

                position += read;
            }

            Assert.True(flash >= 0 && beep >= 0, $"{name}: destello {flash}, pitido {beep}.");
            double offset = Time.FromSamples(beep, AudioDecoder.OutputSampleRate).ToSeconds() - Time.FromFrames(flash, video.FrameRate).ToSeconds();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: destello en el cuadro {flash}, pitido a {offset * 1000:0.0} ms de él"));
            Assert.InRange(Math.Abs(offset), 0, frame.ToSeconds());
        });
    }

    /// <summary>Destello blanco y pitido de 1 kHz a la vez en 1 s, sobre negro y silencio.</summary>
    internal const string FlashAndBeep = "-f lavfi -i color=c=black:s=320x240:r=30:d=3,drawbox=c=white:t=fill:enable=between(t\\,1\\,1.2) "
        + "-f lavfi -i aevalsrc=if(between(t\\,1\\,1.2)\\,0.8*sin(2*PI*1000*t)\\,0):s=48000:d=3";

    private static double Luma(Rodaje.Playback.DecodedFrame frame)
    {
        var pixels = Assert.IsType<CpuFramePayload>(frame.Payload);
        ReadOnlySpan<byte> plane = pixels.GetPlane(0, out int stride);
        int row = stride * (plane.Length / stride / 2);
        long sum = 0;
        for (int x = 0; x < 64; x++)
        {
            sum += plane[row + x];
        }

        return sum / 64.0;
    }

    private static void DecodesEverywhere(string path, MediaInfo info)
    {
        using (ThumbnailDecoder thumbnails = ThumbnailDecoder.Open(path))
        {
            Assert.NotNull(thumbnails.Decode(Time.Zero, 90));
            Assert.NotNull(thumbnails.Decode(Time.FromTicks(info.Duration.Ticks / 2), 90));
        }

        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { AllowHardwareDecoding = false });
        long last = Math.Max(0, decoder.FrameCount - 1);
        foreach (long index in new[] { 0, last / 2, last })
        {
            using var frame = decoder.GetFrame(index);
            Assert.True(frame is not null, $"El cuadro {index} de {decoder.FrameCount} no salió.");
        }
    }

    private string Subtitles()
    {
        string path = Path.Combine(fixture.Directory, "subtitulos.srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:01,500\nHola\n\n2\n00:00:01,500 --> 00:00:03,000\nMundo\n");
        return path;
    }

    // Un cuelgue es un fallo, no una prueba que no termina.
    private static Task Bounded(Action action) => Task.Run(action).WaitAsync(Limit);
}
