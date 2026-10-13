using System.Buffers.Binary;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;
using Rodaje.Tests.Fuzz;

namespace Rodaje.Media.Tests;

/// <summary>
/// MP4 y MKV con cabeceras imposibles (número de cuadros, duraciones, escalas de tiempo, tamaños, códec desconocido,
/// flujos vacíos) y truncados, generados en la prueba a partir de un clip pequeño y válido. El contrato de
/// <see cref="MediaProbe.Probe(string)"/>: o devuelve metadatos con los que se puede trabajar (duraciones y cuadros no
/// negativos, tasa positiva) o lanza <see cref="MediaException"/> con un mensaje claro; nunca otra excepción ni un
/// cuelgue (tarea 102, resto del fuzz de la tarea 94).
/// </summary>
public sealed class MediaProbeBrokenHeaderTests : IDisposable
{
    private const int Seed = 102_000_000;

    private static readonly ClipSpec Mp4 = new("fuzz-h264-25-audio", ClipCodec.H264, 25, 1, 30, Width: 160, Height: 120, Gop: 10,
        BFrames: 0, Audio: ClipAudio.Stereo48k);

    private static readonly ClipSpec Mkv = Mp4 with { Extension = "mkv" };

    private readonly string _directory = Directory.CreateTempSubdirectory("rodaje-sonda-rota-").FullName;
    private int _written;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // --- MP4 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Mp4Cases => [.. Mp4Patches.Keys];

    [FfmpegLibraryTheory]
    [MemberData(nameof(Mp4Cases))]
    public void Mp4_WithImpossibleHeader_GivesMediaExceptionOrSaneInfo(string patch)
    {
        byte[] data = File.ReadAllBytes(TestClips.PathOf(Mp4));
        Assert.True(Mp4Patches[patch](data), $"El clip de prueba no tiene lo que cambia «{patch}».");
        ExpectSane(patch, data, ".mp4");
    }

    // --- MKV -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> MkvCases => [.. MkvPatches.Keys];

    [FfmpegLibraryTheory]
    [MemberData(nameof(MkvCases))]
    public void Mkv_WithImpossibleHeader_GivesMediaExceptionOrSaneInfo(string patch)
    {
        byte[] data = File.ReadAllBytes(TestClips.PathOf(Mkv));
        Assert.True(MkvPatches[patch](data), $"El clip de prueba no tiene lo que cambia «{patch}».");
        ExpectSane(patch, data, ".mkv");
    }

    // --- Truncados -------------------------------------------------------------------------------------------------------

    [FfmpegLibraryFact]
    public void TruncatedFiles_GiveMediaExceptionOrSaneInfo()
    {
        foreach ((ClipSpec spec, string extension) in new[] { (Mp4, ".mp4"), (Mkv, ".mkv") })
        {
            byte[] data = File.ReadAllBytes(TestClips.PathOf(spec));
            foreach (int length in new[] { 0, 1, 4, 8, 12, 16, 31, 32, 40, 64, 100, 200, 500, 1000, 2000, 4096 }
                .Concat(Enumerable.Range(1, 19).Select(k => data.Length * k / 20)))
            {
                ExpectSane($"{extension} truncado a {length} bytes", data[..Math.Min(length, data.Length)], extension);
            }
        }
    }

    // --- Fuzz con semillas fijas -----------------------------------------------------------------------------------------

    /// <summary>
    /// Varios cambios a la vez sobre las cabeceras (con valores al azar en los campos que usa cada cambio), bytes
    /// cambiados y cortes. Cada caso tiene su semilla (ver <see cref="FuzzRunner"/>).
    /// </summary>
    [FfmpegLibraryFact]
    public void MutatedHeaders_OnlyThrowMediaException()
    {
        byte[] mp4 = File.ReadAllBytes(TestClips.PathOf(Mp4));
        byte[] mkv = File.ReadAllBytes(TestClips.PathOf(Mkv));
        FuzzRunner.Run("sonda", Seed, cases: 300, TimeSpan.FromSeconds(60), minimumCases: 60, (seed, rng) =>
        {
            bool isMp4 = rng.Next(2) == 0;
            byte[] data = [.. isMp4 ? mp4 : mkv];
            int headerEnd = isMp4 ? Mp4HeaderEnd(data) : Math.Min(data.Length, 4096);
            int edits = 1 + rng.Next(4);
            for (int k = 0; k < edits; k++)
            {
                switch (rng.Next(4))
                {
                    case 0:
                        RandomFieldPatch(data, isMp4, rng);
                        break;
                    case 1:
                        data[rng.Next(headerEnd)] = (byte)rng.Next(256);
                        break;
                    case 2:
                        int at = rng.Next(Math.Max(1, headerEnd - 8));
                        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at),
                            rng.Next(4) switch { 0 => uint.MaxValue, 1 => 0, 2 => 0x80000000u, _ => (uint)rng.Next() });
                        break;
                    default:
                        if (rng.Next(3) == 0)
                        {
                            data = data[..rng.Next(data.Length)];
                            headerEnd = Math.Min(headerEnd, data.Length);
                        }

                        break;
                }

                if (data.Length == 0)
                {
                    break;
                }
            }

            ExpectSane($"semilla {seed}", data, isMp4 ? ".mp4" : ".mkv");
        });
    }

    // --- Comprobación ----------------------------------------------------------------------------------------------------

    private void ExpectSane(string what, byte[] data, string extension)
    {
        string path = Path.Combine(_directory, $"roto-{_written++}{extension}");
        File.WriteAllBytes(path, data);
        MediaInfo info;
        try
        {
            info = MediaProbe.Probe(path);
        }
        catch (MediaException ex)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), what);
            return;
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"{what}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(info.Video is not null || info.Audio is not null, what);
        Assert.True(info.Duration >= Time.Zero, $"{what}: duración negativa {info.Duration} (video {info.Video?.StartTime} + {info.Video?.Duration}, "
            + $"audio {info.Audio?.StartTime} + {info.Audio?.Duration}, contenedor desde {info.ContainerStartTime})");
        if (info.Video is { } video)
        {
            Assert.True(video.FrameCount >= 0, $"{what}: {video.FrameCount} cuadros");
            Assert.True(video.Duration >= Time.Zero, $"{what}: duración de video negativa {video.Duration}");
            Assert.True(video.FrameRate.Num > 0 && video.FrameRate.Den > 0, $"{what}: tasa {video.FrameRate}");
            Assert.True(video.Width >= 0 && video.Height >= 0 && video.DisplayWidth >= 0 && video.DisplayHeight >= 0,
                $"{what}: tamaño {video.Width}×{video.Height} ({video.DisplayWidth}×{video.DisplayHeight})");
        }

        if (info.Audio is { } audio)
        {
            Assert.True(audio.Duration >= Time.Zero, $"{what}: duración de audio negativa {audio.Duration}");
            Assert.True(audio.SampleRate >= 0 && audio.Channels >= 0, $"{what}: {audio.SampleRate} Hz, {audio.Channels} canales");
        }

        // Lo que se guarda en el proyecto tampoco puede lanzar.
        info.ApplyTo(new MediaRef { Id = "m", Path = path });
    }

    // --- Cambios en MP4 --------------------------------------------------------------------------------------------------

    private static readonly string[] Mp4Containers = ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf"];

    /// <summary>Las cajas del MP4 con su camino («moov/trak/mdia/mdhd»), en orden.</summary>
    private static List<(string Path, int Start, int Size)> Mp4Boxes(byte[] data)
    {
        var boxes = new List<(string, int, int)>();
        void Walk(int from, int to, string parent)
        {
            int at = from;
            while (at + 8 <= to)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                string type = Encoding.Latin1.GetString(data, at + 4, 4);
                if (size == 1 && at + 16 <= to)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                }
                else if (size == 0)
                {
                    size = to - at;
                }

                if (size < 8 || at + size > to)
                {
                    return;
                }

                string path = parent.Length == 0 ? type : parent + "/" + type;
                boxes.Add((path, at, (int)size));
                if (Array.IndexOf(Mp4Containers, type) >= 0)
                {
                    Walk(at + 8, at + (int)size, path);
                }

                at += (int)size;
            }
        }

        Walk(0, data.Length, "");
        return boxes;
    }

    private static int Mp4HeaderEnd(byte[] data)
    {
        List<(string Path, int Start, int Size)> boxes = Mp4Boxes(data);
        int end = boxes.Where(b => b.Path == "moov").Select(b => b.Start + b.Size).DefaultIfEmpty(Math.Min(data.Length, 4096)).First();
        return Math.Max(16, Math.Min(end, data.Length));
    }

    /// <summary>Aplica <paramref name="change"/> a cada caja con ese camino (en las dos pistas si es de pista).</summary>
    private static Func<byte[], bool> EachBox(string path, Action<byte[], int, int> change) => data =>
    {
        bool found = false;
        foreach ((string boxPath, int start, int size) in Mp4Boxes(data))
        {
            if (boxPath == path)
            {
                change(data, start, size);
                found = true;
            }
        }

        return found;
    };

    /// <summary>Solo en la pista de video (la que tiene <c>vmhd</c>).</summary>
    private static Func<byte[], bool> VideoBox(string path, Action<byte[], int, int> change) => data =>
    {
        List<(string Path, int Start, int Size)> boxes = Mp4Boxes(data);
        foreach ((string trakPath, int trakStart, int trakSize) in boxes.Where(b => b.Path == "moov/trak"))
        {
            List<(string Path, int Start, int Size)> inside = [.. boxes.Where(b => b.Start > trakStart && b.Start < trakStart + trakSize)];
            if (inside.Any(b => b.Path.EndsWith("/vmhd", StringComparison.Ordinal)))
            {
                foreach ((string p, int start, int size) in inside.Where(b => b.Path == path))
                {
                    change(data, start, size);
                    return true;
                }
            }
        }

        return false;
    };

    private static void U32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), value);

    private static void U16(byte[] data, int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), value);

    /// <summary>
    /// Campo de duración o de escala de una caja <c>mvhd</c>/<c>mdhd</c>/<c>tkhd</c>, según su versión: en la 0 son
    /// de 32 bits y en la 1, las fechas y la duración son de 64.
    /// </summary>
    private static void HeaderField(byte[] data, int start, string field, ulong value)
    {
        bool v1 = data[start + 8] == 1;
        bool tkhd = Encoding.Latin1.GetString(data, start + 4, 4) == "tkhd";
        int offset = (field, v1, tkhd) switch
        {
            ("timescale", false, false) => 20,
            ("timescale", true, false) => 28,
            ("duration", false, false) => 24,
            ("duration", true, false) => 32,
            ("duration", false, true) => 28,
            ("duration", true, true) => 36,
            _ => throw new ArgumentException(field),
        };
        bool wide = field == "duration" && v1;
        if (wide)
        {
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(start + offset), value);
        }
        else
        {
            U32(data, start + offset, (uint)Math.Min(value, uint.MaxValue));
        }
    }

    private static readonly Dictionary<string, Func<byte[], bool>> Mp4Patches = new()
    {
        ["mvhd duración 0"] = EachBox("moov/mvhd", (d, s, _) => HeaderField(d, s, "duration", 0)),
        ["mvhd duración máxima"] = EachBox("moov/mvhd", (d, s, _) => HeaderField(d, s, "duration", ulong.MaxValue)),
        ["mvhd escala 0"] = EachBox("moov/mvhd", (d, s, _) => HeaderField(d, s, "timescale", 0)),
        ["mvhd escala 1 y duración máxima"] = EachBox("moov/mvhd", (d, s, _) =>
        {
            HeaderField(d, s, "timescale", 1);
            HeaderField(d, s, "duration", ulong.MaxValue);
        }),
        ["mdhd duración máxima"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) => HeaderField(d, s, "duration", ulong.MaxValue)),
        ["mdhd duración 0x80000000"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) => HeaderField(d, s, "duration", 0x80000000)),
        ["mdhd escala 0"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) => HeaderField(d, s, "timescale", 0)),
        ["mdhd escala 1"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) => HeaderField(d, s, "timescale", 1)),
        ["mdhd escala máxima"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) => HeaderField(d, s, "timescale", uint.MaxValue)),
        ["mdhd escala 1 y duración máxima"] = EachBox("moov/trak/mdia/mdhd", (d, s, _) =>
        {
            HeaderField(d, s, "timescale", 1);
            HeaderField(d, s, "duration", uint.MaxValue);
        }),
        ["tkhd duración máxima"] = EachBox("moov/trak/tkhd", (d, s, _) => HeaderField(d, s, "duration", ulong.MaxValue)),
        ["tkhd tamaño máximo"] = EachBox("moov/trak/tkhd", (d, s, size) =>
        {
            U32(d, s + size - 8, uint.MaxValue);
            U32(d, s + size - 4, uint.MaxValue);
        }),
        ["video 0×0"] = VideoBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) =>
        {
            U16(d, s + 16 + 32, 0);
            U16(d, s + 16 + 34, 0);
        }),
        ["video 65535×65535"] = VideoBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) =>
        {
            U16(d, s + 16 + 32, ushort.MaxValue);
            U16(d, s + 16 + 34, ushort.MaxValue);
        }),
        ["códec desconocido"] = EachBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) => Encoding.Latin1.GetBytes("zzzz", 0, 4, d, s + 16 + 4)),
        ["códec de video desconocido"] = VideoBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) => Encoding.Latin1.GetBytes("rdj0", 0, 4, d, s + 16 + 4)),
        ["stsd sin entradas"] = EachBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) => U32(d, s + 12, 0)),
        ["stsd con entradas de más"] = EachBox("moov/trak/mdia/minf/stbl/stsd", (d, s, _) => U32(d, s + 12, uint.MaxValue)),
        ["stsz sin muestras"] = EachBox("moov/trak/mdia/minf/stbl/stsz", (d, s, _) => U32(d, s + 16, 0)),
        ["stsz con muestras de más"] = EachBox("moov/trak/mdia/minf/stbl/stsz", (d, s, _) => U32(d, s + 16, uint.MaxValue)),
        ["stsz tamaño fijo enorme"] = EachBox("moov/trak/mdia/minf/stbl/stsz", (d, s, _) => U32(d, s + 12, uint.MaxValue)),
        ["stts sin entradas"] = EachBox("moov/trak/mdia/minf/stbl/stts", (d, s, _) => U32(d, s + 12, 0)),
        ["stts cuenta máxima"] = EachBox("moov/trak/mdia/minf/stbl/stts", (d, s, _) => U32(d, s + 16, uint.MaxValue)),
        ["stts delta 0"] = EachBox("moov/trak/mdia/minf/stbl/stts", (d, s, _) => U32(d, s + 20, 0)),
        ["stts delta máxima"] = EachBox("moov/trak/mdia/minf/stbl/stts", (d, s, _) => U32(d, s + 20, uint.MaxValue)),
        ["stts delta negativa"] = EachBox("moov/trak/mdia/minf/stbl/stts", (d, s, _) => U32(d, s + 20, 0x80000000)),
        ["stco fuera del archivo"] = EachBox("moov/trak/mdia/minf/stbl/stco", (d, s, size) =>
        {
            for (int at = s + 16; at + 4 <= s + size; at += 4)
            {
                U32(d, at, uint.MaxValue - 16);
            }
        }),
        ["stco con entradas de más"] = EachBox("moov/trak/mdia/minf/stbl/stco", (d, s, _) => U32(d, s + 12, uint.MaxValue)),
        ["stsc sin entradas"] = EachBox("moov/trak/mdia/minf/stbl/stsc", (d, s, _) => U32(d, s + 12, 0)),
        ["hdlr desconocido"] = EachBox("moov/trak/mdia/hdlr", (d, s, _) => Encoding.Latin1.GetBytes("zzzz", 0, 4, d, s + 16)),
        ["sin pistas"] = EachBox("moov/trak", (d, s, _) => Encoding.Latin1.GetBytes("free", 0, 4, d, s + 4)),
        ["sin moov"] = EachBox("moov", (d, s, _) => Encoding.Latin1.GetBytes("free", 0, 4, d, s + 4)),
        ["mdat vacío"] = EachBox("mdat", (d, s, size) => Array.Clear(d, s + 8, size - 8)),
        ["moov tamaño máximo"] = EachBox("moov", (d, s, _) => U32(d, s, uint.MaxValue)),
        ["trak tamaño 9"] = EachBox("moov/trak", (d, s, _) => U32(d, s, 9)),
    };

    private static readonly string[] Mp4Fields = ["moov/mvhd", "moov/trak/mdia/mdhd", "moov/trak/tkhd"];

    private static readonly string[] Mp4Tables =
        ["moov/trak/mdia/minf/stbl/stsz", "moov/trak/mdia/minf/stbl/stts", "moov/trak/mdia/minf/stbl/stco", "moov/trak/mdia/minf/stbl/stsc", "moov/trak/mdia/minf/stbl/stsd"];

    private static ulong RandomValue(Random rng) => rng.Next(7) switch
    {
        0 => 0,
        1 => 1,
        2 => uint.MaxValue,
        3 => 0x80000000,
        4 => ulong.MaxValue,
        5 => (ulong)long.MaxValue,
        _ => (ulong)rng.NextInt64(),
    };

    private static void RandomFieldPatch(byte[] data, bool isMp4, Random rng)
    {
        if (isMp4)
        {
            List<(string Path, int Start, int Size)> boxes = Mp4Boxes(data);
            bool header = rng.Next(2) == 0;
            List<(string Path, int Start, int Size)> candidates =
                [.. boxes.Where(b => Array.IndexOf(header ? Mp4Fields : Mp4Tables, b.Path) >= 0)];
            if (candidates.Count == 0)
            {
                return;
            }

            (string path, int start, int size) = candidates[rng.Next(candidates.Count)];
            if (header)
            {
                HeaderField(data, start, path.EndsWith("tkhd", StringComparison.Ordinal) || rng.Next(2) == 0 ? "duration" : "timescale", RandomValue(rng));
            }
            else if (size > 16)
            {
                int at = Math.Min(start + 12 + 4 * rng.Next((size - 12) / 4), start + size - 4);
                U32(data, at, (uint)Math.Min(RandomValue(rng), uint.MaxValue));
            }

            return;
        }

        List<Ebml> elements = [.. MkvElements(data).Where(e => MkvNumbers.Contains(e.Id) && e.Size is > 0 and <= 8)];
        if (elements.Count == 0)
        {
            return;
        }

        Ebml element = elements[rng.Next(elements.Count)];
        if (element.Id is DurationId or SamplingFrequencyId && element.Size is 4 or 8 && rng.Next(2) == 0)
        {
            WriteFloat(data, element, RandomFloat(rng));
        }
        else
        {
            WriteNumber(data, element, RandomValue(rng));
        }
    }

    private static float RandomFloat(Random rng) => rng.Next(6) switch
    {
        0 => float.NaN,
        1 => float.PositiveInfinity,
        2 => float.NegativeInfinity,
        3 => -1,
        4 => float.MaxValue,
        _ => 0,
    };

    // --- Cambios en MKV --------------------------------------------------------------------------------------------------

    private const uint SegmentId = 0x18538067;
    private const uint InfoId = 0x1549A966;
    private const uint TimecodeScaleId = 0x2AD7B1;
    private const uint DurationId = 0x4489;
    private const uint TracksId = 0x1654AE6B;
    private const uint TrackEntryId = 0xAE;
    private const uint TrackTypeId = 0x83;
    private const uint CodecId = 0x86;
    private const uint DefaultDurationId = 0x23E383;
    private const uint VideoId = 0xE0;
    private const uint PixelWidthId = 0xB0;
    private const uint PixelHeightId = 0xBA;
    private const uint AudioId = 0xE1;
    private const uint SamplingFrequencyId = 0xB5;
    private const uint ChannelsId = 0x9F;
    private const uint ClusterId = 0x1F43B675;
    private const uint ClusterTimecodeId = 0xE7;
    private const uint SimpleBlockId = 0xA3;

    private static readonly uint[] MkvMasters = [SegmentId, InfoId, TracksId, TrackEntryId, VideoId, AudioId, ClusterId];

    private static readonly HashSet<uint> MkvNumbers =
        [TimecodeScaleId, DurationId, TrackTypeId, DefaultDurationId, PixelWidthId, PixelHeightId, SamplingFrequencyId, ChannelsId, ClusterTimecodeId];

    /// <summary>Un elemento EBML: su id, dónde empiezan sus datos, cuántos bytes ocupan y el id de la pista de su padre.</summary>
    private readonly record struct Ebml(uint Id, int Data, int Size, uint Parent);

    private static List<Ebml> MkvElements(byte[] data)
    {
        var elements = new List<Ebml>();
        void Walk(int from, int to, uint parent, int depth)
        {
            int at = from;
            while (at < to && depth < 8)
            {
                if (!ReadVint(data, at, to, keepMarker: true, out ulong id, out int idLength)
                    || !ReadVint(data, at + idLength, to, keepMarker: false, out ulong size, out int sizeLength))
                {
                    return;
                }

                int start = at + idLength + sizeLength;
                // Tamaño desconocido (todo unos): hasta el final del padre.
                long end = size == (1UL << (7 * sizeLength)) - 1 ? to : start + (long)size;
                if (end > to)
                {
                    end = to;
                }

                elements.Add(new Ebml((uint)id, start, (int)(end - start), parent));
                if (Array.IndexOf(MkvMasters, (uint)id) >= 0)
                {
                    // Los clusters solo interesan por su primera marca; no hace falta entrar en todos.
                    if ((uint)id != ClusterId || elements.Count(e => e.Id == ClusterId) <= 2)
                    {
                        Walk(start, (int)end, (uint)id, depth + 1);
                    }
                }

                at = (int)end;
            }
        }

        Walk(0, data.Length, 0, 0);
        return elements;
    }

    private static bool ReadVint(byte[] data, int at, int to, bool keepMarker, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (at >= to)
        {
            return false;
        }

        byte first = data[at];
        length = first == 0 ? 9 : System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
        if (length > 8 || at + length > to)
        {
            return false;
        }

        value = keepMarker ? first : (ulong)(first & (0xFF >> length));
        for (int k = 1; k < length; k++)
        {
            value = (value << 8) | data[at + k];
        }

        return true;
    }

    private static void WriteNumber(byte[] data, Ebml element, ulong value)
    {
        int bits = element.Size * 8;
        ulong clipped = bits >= 64 ? value : Math.Min(value, (1UL << bits) - 1);
        for (int k = element.Size - 1; k >= 0; k--)
        {
            data[element.Data + k] = (byte)clipped;
            clipped >>= 8;
        }
    }

    private static void WriteFloat(byte[] data, Ebml element, double value)
    {
        if (element.Size == 4)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(element.Data), (float)value);
        }
        else if (element.Size == 8)
        {
            BinaryPrimitives.WriteDoubleBigEndian(data.AsSpan(element.Data), value);
        }
    }

    private static Func<byte[], bool> EachElement(uint id, Action<byte[], Ebml> change, Func<byte[], Ebml, bool>? where = null) => data =>
    {
        bool found = false;
        foreach (Ebml element in MkvElements(data))
        {
            if (element.Id == id && (where is null || where(data, element)))
            {
                change(data, element);
                found = true;
            }
        }

        return found;
    };

    /// <summary>Los hijos de la entrada de pista de video (<c>TrackType</c> = 1).</summary>
    private static bool InVideoTrack(byte[] data, Ebml element)
    {
        List<Ebml> elements = MkvElements(data);
        foreach (Ebml entry in elements.Where(e => e.Id == TrackEntryId))
        {
            bool inside = element.Data > entry.Data && element.Data < entry.Data + entry.Size;
            bool video = elements.Any(e => e.Id == TrackTypeId && e.Data > entry.Data && e.Data < entry.Data + entry.Size && data[e.Data + e.Size - 1] == 1);
            if (inside)
            {
                return video;
            }
        }

        return false;
    }

    private static readonly Dictionary<string, Func<byte[], bool>> MkvPatches = new()
    {
        ["duración NaN"] = EachElement(DurationId, (d, e) => WriteFloat(d, e, double.NaN)),
        ["duración infinita"] = EachElement(DurationId, (d, e) => WriteFloat(d, e, double.PositiveInfinity)),
        ["duración negativa"] = EachElement(DurationId, (d, e) => WriteFloat(d, e, -1e9)),
        ["duración enorme"] = EachElement(DurationId, (d, e) => WriteFloat(d, e, float.MaxValue)),
        ["duración 0"] = EachElement(DurationId, (d, e) => WriteFloat(d, e, 0)),
        ["escala de tiempo 0"] = EachElement(TimecodeScaleId, (d, e) => WriteNumber(d, e, 0)),
        ["escala de tiempo máxima"] = EachElement(TimecodeScaleId, (d, e) => WriteNumber(d, e, ulong.MaxValue)),
        ["códec desconocido"] = EachElement(CodecId, (d, e) => Array.Fill(d, (byte)'Z', e.Data + 2, e.Size - 2)),
        ["códec de video desconocido"] = EachElement(CodecId, (d, e) => Array.Fill(d, (byte)'Z', e.Data + 2, e.Size - 2), InVideoTrack),
        ["códec vacío"] = EachElement(CodecId, (d, e) => Array.Clear(d, e.Data, e.Size)),
        ["pistas de tipo desconocido"] = EachElement(TrackTypeId, (d, e) => WriteNumber(d, e, 0x55)),
        ["video 0×0"] = data => EachElement(PixelWidthId, (d, e) => WriteNumber(d, e, 0))(data)
            & EachElement(PixelHeightId, (d, e) => WriteNumber(d, e, 0))(data),
        ["video de tamaño máximo"] = data => EachElement(PixelWidthId, (d, e) => WriteNumber(d, e, ulong.MaxValue))(data)
            & EachElement(PixelHeightId, (d, e) => WriteNumber(d, e, ulong.MaxValue))(data),
        ["duración de cuadro 0"] = EachElement(DefaultDurationId, (d, e) => WriteNumber(d, e, 0)),
        ["duración de cuadro 1 ns"] = EachElement(DefaultDurationId, (d, e) => WriteNumber(d, e, 1)),
        ["duración de cuadro máxima"] = EachElement(DefaultDurationId, (d, e) => WriteNumber(d, e, ulong.MaxValue)),
        ["audio a 0 Hz"] = EachElement(SamplingFrequencyId, (d, e) => WriteFloat(d, e, 0)),
        ["audio a NaN Hz"] = EachElement(SamplingFrequencyId, (d, e) => WriteFloat(d, e, double.NaN)),
        ["audio a -48000 Hz"] = EachElement(SamplingFrequencyId, (d, e) => WriteFloat(d, e, -48000)),
        ["audio a 1e300 Hz"] = EachElement(SamplingFrequencyId, (d, e) => WriteFloat(d, e, e.Size == 8 ? 1e300 : float.MaxValue)),
        ["audio sin canales"] = EachElement(ChannelsId, (d, e) => WriteNumber(d, e, 0)),
        ["audio con 255 canales"] = EachElement(ChannelsId, (d, e) => WriteNumber(d, e, 255)),
        ["marca de cluster máxima"] = EachElement(ClusterTimecodeId, (d, e) => WriteNumber(d, e, ulong.MaxValue)),
        ["clusters después del final"] = EachElement(ClusterTimecodeId, (d, e) => WriteNumber(d, e, e.Size >= 3 ? 100_000UL : (1UL << (8 * e.Size)) - 1)),
        // El primer cuadro de video dice estar a los 26 s (marcas irregulares: tasa variable) y el contenedor dura 1,2 s:
        // la duración del video, que se toma de lo que queda del contenedor, salía negativa (fuzz, semilla 102004674).
        ["video que empieza después del final"] = data =>
        {
            Ebml first = MkvElements(data).FirstOrDefault(e => e.Id == SimpleBlockId && e.Size > 4 && data[e.Data] == 0x81);
            if (first.Size == 0)
            {
                return false;
            }

            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(first.Data + 1), 26_112);
            return true;
        },
        ["marca de cluster de 63 bits"] = EachElement(ClusterTimecodeId, (d, e) => WriteNumber(d, e, long.MaxValue)),
        ["segmento de tamaño 0"] = EachElement(SegmentId, (d, e) =>
        {
            // El tamaño va justo antes de los datos; se deja con su longitud y valor 0.
            int sizeAt = e.Data - 1;
            while (sizeAt > 0 && d[sizeAt - 1] != 0x67)
            {
                sizeAt--;
            }

            int length = e.Data - sizeAt;
            d[sizeAt] = (byte)(0x80 >> (length - 1));
            Array.Clear(d, sizeAt + 1, length - 1);
        }),
    };
}
