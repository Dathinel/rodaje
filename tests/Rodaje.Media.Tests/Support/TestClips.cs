using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Support;

internal enum ClipCodec
{
    H264,
    Hevc,
    Hevc10,
}

internal enum ClipAudio
{
    None,

    /// <summary>AAC estéreo a 48 kHz: el decodificador no remuestrea y la comparación es exacta.</summary>
    Stereo48k,
}

/// <summary>Clip sintético (patrón testsrc2) que se genera una vez y se guarda en la caché.</summary>
internal sealed record ClipSpec(
    string Name,
    ClipCodec Codec,
    int RateNum,
    int RateDen,
    int Frames,
    int Width = 640,
    int Height = 360,
    int Gop = 48,
    int BFrames = 2,
    ClipAudio Audio = ClipAudio.None,
    string Extension = "mp4")
{
    public string FileName => $"{Name}.{Extension}";

    public double Seconds => (double)Frames * RateDen / RateNum;

    public bool TenBit => Codec == ClipCodec.Hevc10;

    public override string ToString() => Name;
}

/// <summary>Genera clips con el ffmpeg.exe LGPL y sus referencias por decodificación de software.</summary>
/// <remarks>
/// La referencia de video es el hash del plano Y de cada cuadro, en orden de presentación, tal como lo
/// decodifica ffmpeg.exe sin aceleración (<c>-fps_mode passthrough</c>, sin duplicar ni descartar).
/// </remarks>
internal static class TestClips
{
    public static readonly ClipSpec H264 = new("h264-2997-g48-bf2", ClipCodec.H264, 30000, 1001, 300, Audio: ClipAudio.Stereo48k);
    public static readonly ClipSpec Hevc = new("hevc-25-g48-bf2", ClipCodec.Hevc, 25, 1, 250);
    public static readonly ClipSpec Hevc10 = new("hevc10-25-g24-bf2", ClipCodec.Hevc10, 25, 1, 120, Gop: 24);
    public static readonly ClipSpec H264Mkv = new("h264-25-g30-bf2", ClipCodec.H264, 25, 1, 200, Gop: 30, Extension: "mkv");

    private static readonly ConcurrentDictionary<string, Lazy<string>> Generated = new();
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> References = new(StringComparer.OrdinalIgnoreCase);

    public static string PathOf(ClipSpec spec) =>
        Generated.GetOrAdd(spec.FileName, _ => new Lazy<string>(() => Generate(spec))).Value;

    /// <summary>Hash del plano Y (en 8 bits) de cada cuadro, en orden de presentación.</summary>
    public static IReadOnlyList<ulong> LumaReference(ClipSpec spec)
    {
        string clip = PathOf(spec);
        string cache = clip + ".luma.txt";
        return Cached(cache, () =>
        {
            var hashes = new List<ulong>();
            int bytesPerSample = spec.TenBit ? 2 : 1;
            int frameBytes = spec.Width * spec.Height * bytesPerSample;
            RunFfmpeg(stdout =>
            {
                var buffer = new byte[frameBytes];
                while (ReadExactly(stdout, buffer))
                {
                    hashes.Add(HashLuma(spec.TenBit ? To8Bit(buffer) : buffer));
                }
            }, "-v", "error", "-i", clip, "-map", "0:v:0", "-fps_mode", "passthrough", "-vf", "extractplanes=y", "-f", "rawvideo", "-");
            return hashes.Select(h => h.ToString("X16", CultureInfo.InvariantCulture)).ToList();
        }).Select(l => ulong.Parse(l, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Índices (en orden de presentación) de los cuadros clave.</summary>
    public static IReadOnlyList<int> KeyFrames(ClipSpec spec)
    {
        string clip = PathOf(spec);
        IReadOnlyList<string> flags = Cached(clip + ".keys.txt", () =>
        {
            string output = RunTool(MediaTestEnvironment.FfprobeExe, "-v", "error", "-select_streams", "v:0",
                "-show_entries", "frame=key_frame", "-of", "csv=p=0", clip);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        });
        return flags.Select((f, i) => (f, i)).Where(x => x.f.StartsWith('1')).Select(x => x.i).ToList();
    }

    /// <summary>Audio decodificado por ffmpeg.exe a float estéreo 48 kHz, desde la primera muestra del flujo.</summary>
    public static float[] AudioReference(string clip)
    {
        string cache = clip + ".f32";
        if (!File.Exists(cache))
        {
            using var memory = new MemoryStream();
            RunFfmpeg(stdout => stdout.CopyTo(memory),
                "-v", "error", "-i", clip, "-map", "0:a:0", "-f", "f32le", "-ac", "2", "-ar", "48000", "-");
            WriteAtomically(cache, memory.ToArray());
        }

        byte[] bytes = File.ReadAllBytes(cache);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>Audio solo, AAC mono a 44,1 kHz: obliga a remuestrear.</summary>
    public static string MonoAudio44k() => AudioOnly("mono-44k.m4a", "sine=frequency=997:sample_rate=44100:duration=4", "aac");

    /// <summary>FLAC estéreo a 48 kHz: sin pérdida y sin estado entre bloques, así que el seek debe ser exacto al bit.</summary>
    public static string FlacStereo48k() =>
        AudioOnly("flac-48k-estereo.mkv", "aevalsrc=exprs=0.5*sin(2*PI*440*t)|0.4*sin(2*PI*(600+150*t)*t):s=48000:d=6", "flac");

    /// <summary>FLAC mono a 44,1 kHz, para medir el remuestreo sin el ruido del códec.</summary>
    public static string FlacMono44k() =>
        AudioOnly("flac-44k-mono.mkv", "aevalsrc=exprs=0.6*sin(2*PI*997*t)+0.2*sin(2*PI*3001*t):s=44100:d=4", "flac");

    private static string AudioOnly(string fileName, string source, string codec) => Generated.GetOrAdd(fileName, name => new Lazy<string>(() =>
        Produce(name, temp => Try(MediaTestEnvironment.FfmpegExe, "-v", "error", "-y", "-f", "lavfi", "-i", source, "-c:a", codec, temp)))).Value;

    /// <summary>
    /// AAC con las marcas de <see cref="Rodaje.TestMedia.AudioCode"/> en dos tramos unidos con el demuxer concat: el
    /// primero cubre [0 s, 3 s) y el segundo empieza en <c>3 s + desplazamiento</c> con el contenido que corresponde
    /// a ese instante. Un desplazamiento positivo deja un hueco en las marcas; uno negativo, un solape.
    /// </summary>
    public static string AacWithTimestampJump(double offsetSeconds, string extension) => Generated.GetOrAdd(
        string.Create(CultureInfo.InvariantCulture, $"aac-salto{offsetSeconds:+0.000;-0.000}.{extension}"),
        name => new Lazy<string>(() => Produce(name, temp =>
        {
            const int Rate = 48_000;
            string work = Path.Combine(MediaTestEnvironment.CacheDirectory, $"tmp-salto-{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            try
            {
                double second = 3 + offsetSeconds;
                (string Name, long First, long Count)[] parts = [("a", 0, 3L * Rate), ("b", (long)Math.Round(second * Rate), 4L * Rate)];
                foreach ((string part, long first, long count) in parts)
                {
                    var samples = new float[count * 2];
                    Rodaje.TestMedia.AudioCode.Generate(samples, first, Rate, 2);
                    var bytes = new byte[samples.Length * sizeof(float)];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(Path.Combine(work, part + ".raw"), bytes);
                    if (!Try(MediaTestEnvironment.FfmpegExe, "-v", "error", "-y", "-f", "f32le", "-ar", "48000", "-ac", "2",
                        "-i", Path.Combine(work, part + ".raw"), "-c:a", "aac", "-b:a", "192k", Path.Combine(work, part + ".mkv")))
                    {
                        return false;
                    }
                }

                File.WriteAllText(Path.Combine(work, "lista.txt"), string.Create(CultureInfo.InvariantCulture,
                    $"file 'a.mkv'\nduration {second:0.000}\nfile 'b.mkv'\n"));
                return Try(MediaTestEnvironment.FfmpegExe, "-v", "error", "-y", "-f", "concat", "-safe", "0",
                    "-i", Path.Combine(work, "lista.txt"), "-c", "copy", temp);
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }))).Value;

    /// <summary>El clip H.264 reempaquetado con la matriz y el rango que declara su VUI cambiados (sin recodificar).</summary>
    /// <param name="matrixCoefficients">Código H.273: 1 = BT.709, 6 = BT.601 (SMPTE 170M).</param>
    public static string TaggedColor(int matrixCoefficients, bool fullRange) => Generated.GetOrAdd(
        string.Create(CultureInfo.InvariantCulture, $"color-m{matrixCoefficients}-{(fullRange ? "pc" : "tv")}.mp4"),
        name => new Lazy<string>(() => Produce(name, temp => Try(MediaTestEnvironment.FfmpegExe, "-v", "error", "-y",
            "-i", PathOf(H264), "-map", "0:v", "-c", "copy",
            "-bsf:v", string.Create(CultureInfo.InvariantCulture,
                $"h264_metadata=video_full_range_flag={(fullRange ? 1 : 0)}:matrix_coefficients={matrixCoefficients}:colour_primaries={matrixCoefficients}:transfer_characteristics={matrixCoefficients}"),
            temp)))).Value;

    /// <summary>Video de tasa variable: cada quinto cuadro llega 10 ms tarde.</summary>
    public static string VariableFrameRate() => Generated.GetOrAdd("vfr-30.mp4", name => new Lazy<string>(() =>
        Produce(name, temp => EncodeVideo(temp, ClipCodec.H264, gop: 30, bFrames: 0,
            ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-frames:v", "90"],
            ["-vf", "settb=1/3000,setpts=N/30/TB+eq(mod(N\\,5)\\,0)*0.01/TB", "-fps_mode", "vfr", "-enc_time_base:v", "filter", "-video_track_timescale", "3000"])))).Value;

    /// <summary>El clip H.264 reempaquetado con rotación de 90° en sentido horario (270° antihorario para ffmpeg) y SAR 4:3.</summary>
    public static string RotatedAnamorphic() => Generated.GetOrAdd("rotado90-sar43.mp4", name => new Lazy<string>(() =>
        Produce(name, temp => Try(MediaTestEnvironment.FfmpegExe, "-v", "error", "-y", "-display_rotation", "270",
            "-i", PathOf(H264), "-c", "copy", "-bsf:v", "h264_metadata=sample_aspect_ratio=4/3", temp)))).Value;

    /// <summary>Cuadros (en orden de presentación) cuyos paquetes de video daña <see cref="Corrupted"/>.</summary>
    public static IReadOnlyList<int> CorruptedFrames { get; } = [100, 105, 110, 121, 130, 150, 151, 165, 170, 180];

    /// <summary>
    /// Copia del clip con paquetes de video dañados por dentro: a la mitad se les rompe el prefijo de longitud de
    /// la primera NAL (el decodificador rechaza el paquete) y a la otra mitad se les llena de basura el centro.
    /// </summary>
    public static string Corrupted(ClipSpec spec) => Generated.GetOrAdd("danado-paquetes-" + spec.FileName, name => new Lazy<string>(() =>
        Produce(name, temp =>
        {
            string clip = PathOf(spec);
            byte[] bytes = File.ReadAllBytes(clip);
            var random = new Random(1234);
            List<(double Pts, long Position, int Size)> packets = VideoPackets(clip);
            double first = packets.Min(p => p.Pts);
            double rate = (double)spec.RateNum / spec.RateDen;
            int damaged = 0;
            foreach ((double pts, long position, int size) in packets)
            {
                int which = CorruptedFrames.ToList().IndexOf((int)Math.Round((pts - first) * rate));
                if (which < 0 || size < 64)
                {
                    continue;
                }

                if (which % 2 == 0)
                {
                    bytes.AsSpan((int)position, 4).Fill(0xFF);
                }
                else
                {
                    int from = (int)position + (size / 4);
                    for (int i = 0; i < size / 2; i++)
                    {
                        bytes[from + i] = (byte)random.Next(256);
                    }
                }

                damaged++;
            }

            if (damaged != CorruptedFrames.Count)
            {
                throw new InvalidOperationException($"Solo se encontraron {damaged} de {CorruptedFrames.Count} paquetes para dañar en {clip}.");
            }

            File.WriteAllBytes(temp, bytes);
            return true;
        }))).Value;

    /// <summary>Paquetes del primer video según ffprobe: marca de presentación en segundos, posición y tamaño.</summary>
    private static List<(double Pts, long Position, int Size)> VideoPackets(string clip)
    {
        string output = RunTool(MediaTestEnvironment.FfprobeExe, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,pos,size", "-of", "csv=p=0", clip);
        var packets = new List<(double, long, int)>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(',');
            packets.Add((
                double.Parse(fields[0], CultureInfo.InvariantCulture),
                long.Parse(fields[2], CultureInfo.InvariantCulture),
                int.Parse(fields[1], CultureInfo.InvariantCulture)));
        }

        return packets;
    }

    public static ulong HashLuma(ReadOnlySpan<byte> luma)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(luma, digest);
        return BitConverter.ToUInt64(digest);
    }

    private static string Generate(ClipSpec spec) => Produce(spec.FileName, temp =>
    {
        var input = new List<string>
        {
            "-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture,
                $"testsrc2=size={spec.Width}x{spec.Height}:rate={spec.RateNum}/{spec.RateDen}"),
        };
        var output = new List<string> { "-frames:v", spec.Frames.ToString(CultureInfo.InvariantCulture) };
        if (spec.Audio == ClipAudio.Stereo48k)
        {
            input.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture,
                $"aevalsrc=exprs=0.5*sin(2*PI*440*t)|0.4*sin(2*PI*(600+150*t)*t):s=48000:d={spec.Seconds:0.###}")]);
            output.AddRange(["-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", "160k"]);
        }

        return EncodeVideo(temp, spec.Codec, spec.Gop, spec.BFrames, input, output);
    });

    // Prueba primero NVENC (B-frames de verdad) y después los codificadores de software del build LGPL.
    private static bool EncodeVideo(string temp, ClipCodec codec, int gop, int bFrames, IList<string> input, IList<string> output)
    {
        string g = gop.ToString(CultureInfo.InvariantCulture);
        string bf = bFrames.ToString(CultureInfo.InvariantCulture);
        List<string[]> encoders = codec switch
        {
            ClipCodec.H264 =>
            [
                ["-c:v", "h264_nvenc", "-g", g, "-bf", bf, "-pix_fmt", "yuv420p", "-b:v", "3M"],
                ["-c:v", "libopenh264", "-g", g, "-pix_fmt", "yuv420p", "-b:v", "3M"],
            ],
            ClipCodec.Hevc =>
            [
                ["-c:v", "hevc_nvenc", "-g", g, "-bf", bf, "-pix_fmt", "yuv420p", "-b:v", "3M"],
                ["-c:v", "libkvazaar", "-g", g, "-pix_fmt", "yuv420p", "-b:v", "3M"],
            ],
            _ => [["-c:v", "hevc_nvenc", "-profile:v", "main10", "-g", g, "-bf", bf, "-pix_fmt", "p010le", "-b:v", "3M"]],
        };
        foreach (string[] encoder in encoders)
        {
            if (Try(MediaTestEnvironment.FfmpegExe, ["-v", "error", "-y", .. input, .. encoder, .. output, temp]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Produce(string name, Func<string, bool> make)
    {
        string target = Path.Combine(MediaTestEnvironment.CacheDirectory, name);
        if (File.Exists(target))
        {
            return target;
        }

        using CrossProcessLock fileLock = CrossProcessLock.Acquire(target);
        if (File.Exists(target))
        {
            return target;
        }

        string temp = Path.Combine(MediaTestEnvironment.CacheDirectory, $"tmp-{Guid.NewGuid():N}{Path.GetExtension(name)}");
        try
        {
            if (!make(temp) || !File.Exists(temp))
            {
                throw new InvalidOperationException($"No se pudo generar el clip de prueba {name} con {MediaTestEnvironment.FfmpegExe}.");
            }

            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            File.Delete(temp);
        }

        return target;
    }

    // Una sola vez por archivo y proceso: dos pruebas en paralelo con la caché fría chocaban al renombrar el temporal
    // sobre el archivo que la otra estaba leyendo.
    private static IReadOnlyList<string> Cached(string path, Func<List<string>> compute) =>
        References.GetOrAdd(path, p => new Lazy<IReadOnlyList<string>>(() =>
        {
            if (File.Exists(p))
            {
                return File.ReadAllLines(p);
            }

            List<string> lines = compute();
            WriteAtomically(p, Encoding.UTF8.GetBytes(string.Join('\n', lines)));
            return lines;
        })).Value;

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] To8Bit(byte[] tenBitLittleEndian)
    {
        var result = new byte[tenBitLittleEndian.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((tenBitLittleEndian[2 * i] | (tenBitLittleEndian[(2 * i) + 1] << 8)) >> 2);
        }

        return result;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }

    private static bool Try(string exe, params string[] args)
    {
        using Process process = Start(exe, args);
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static string RunTool(string exe, params string[] args)
    {
        using Process process = Start(exe, args);
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} terminó con código {process.ExitCode}.");
        }

        return output;
    }

    private static void RunFfmpeg(Action<Stream> consume, params string[] args)
    {
        using Process process = Start(MediaTestEnvironment.FfmpegExe, args);
        consume(process.StandardOutput.BaseStream);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg.exe terminó con código {process.ExitCode}: {string.Join(' ', args)}");
        }
    }

    private static Process Start(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}.");
    }
}
