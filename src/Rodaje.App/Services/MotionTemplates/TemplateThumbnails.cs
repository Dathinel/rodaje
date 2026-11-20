using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Packages.Content;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;

namespace Rodaje.App.Services.MotionTemplates;

/// <summary>Los cuadros de una miniatura animada, uno tras otro en horizontal, en RGBA de 8 bits.</summary>
public sealed record TemplateStrip(int FrameWidth, int FrameHeight, int Frames, byte[] Rgba)
{
    /// <summary>El cuadro <paramref name="index"/> en BGRA, para un <c>WriteableBitmap</c>.</summary>
    public byte[] FrameBgra(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Frames);
        var frame = new byte[FrameWidth * FrameHeight * 4];
        int stride = FrameWidth * Frames * 4;
        for (int y = 0; y < FrameHeight; y++)
        {
            int from = (y * stride) + (index * FrameWidth * 4);
            int to = y * FrameWidth * 4;
            for (int x = 0; x < FrameWidth * 4; x += 4)
            {
                frame[to + x] = Rgba[from + x + 2];
                frame[to + x + 1] = Rgba[from + x + 1];
                frame[to + x + 2] = Rgba[from + x];
                frame[to + x + 3] = Rgba[from + x + 3];
            }
        }

        return frame;
    }
}

/// <summary>
/// La miniatura animada de una plantilla de títulos (D-064): 12 cuadros a 192 × 108 con los valores por defecto, compuestos
/// como un cuadro de exportación (<see cref="OfflineTimeline"/> y el compositor) y guardados como tira PNG en
/// <c>cache\plantillas\&lt;sha256 del documento&gt;.png</c>. Nada de esto corre en el hilo de la interfaz.
/// </summary>
public static class TemplateThumbnails
{
    public const int Frames = 12;
    public const int Width = 192;
    public const int Height = 108;
    /// <summary>Sube cuando cambia cómo se compone la tira: las guardadas con otra versión se vuelven a hacer.</summary>
    public const int Version = 1;

    /// <summary>Tope de la carpeta de la caché; al pasarlo se borran las tiras más viejas.</summary>
    public const long MaxCacheBytes = 64L * 1024 * 1024;
    private const string SequenceId = "miniatura";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly string FramesKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RodajeTira{Version}\0");

    public static string CacheFolder => Path.Combine(AppDataPaths.Root, "cache", "plantillas");

    public static string CachePath(string folder, string documentText)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(documentText);
        return Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(documentText))) + ".png");
    }

    /// <summary>La tira guardada de ese documento; nula si no está o no es una tira de las de aquí.</summary>
    public static TemplateStrip? Load(string folder, string documentText)
    {
        try
        {
            string path = CachePath(folder, documentText);
            return File.Exists(path) && new FileInfo(path).Length < 16 * 1024 * 1024 ? DecodePng(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string folder, string documentText, TemplateStrip strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        Directory.CreateDirectory(folder);
        string path = CachePath(folder, documentText);
        // Nombre propio: dos hilos que guarden la misma tira no se pisan el temporal.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, EncodePng(strip));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            // Otro hilo acaba de dejar la misma tira (mismo documento, mismo contenido): vale la suya.
        }
        finally
        {
            File.Delete(temp);
        }

        Media.DiskCacheTrimmer.Trim(folder, "*.png", MaxCacheBytes);
    }

    /// <summary>Un proyecto de 192 × 108 con la plantilla sola, con sus valores por defecto y su duración.</summary>
    public static Project BuildProject(MotionTemplateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Time duration = Time.FromSeconds(Math.Max(entry.Document.DurationSeconds, 0.5));
        var track = new Track { Id = "v1", Kind = TrackKind.Video, Name = "Plantilla" };
        track.Clips.Add(new Clip { Id = "c1", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = duration, Generator = TemplateInstances.NewGenerator(entry) });
        var sequence = new Sequence { Id = SequenceId, Name = "Miniatura", FrameRate = Rational.Fps30, Width = Width, Height = Height };
        sequence.VideoTracks.Add(track);
        return new Project { Id = "miniatura", Name = "Miniatura", Sequences = [sequence] };
    }

    /// <summary>
    /// Compone las tiras de <paramref name="entries"/> (solo las de títulos) con un dispositivo propio y entrega cada una
    /// en cuanto está. Bloquea: se llama desde un hilo de fondo.
    /// </summary>
    public static void Render(IReadOnlyList<MotionTemplateEntry> entries, IReadOnlyList<string> fontFiles, Action<MotionTemplateEntry, TemplateStrip> done,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fontFiles);
        ArgumentNullException.ThrowIfNull(done);
        if (!entries.Any(e => e.Kind == TemplateKind.Title))
        {
            return;
        }

        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu);
        if (fontFiles.Count > 0)
        {
            titles.RegisterFonts(fontFiles);
        }

        using var compositor = new Compositor(gpu, Width, Height) { Titles = titles };
        if (!compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60), cancellationToken))
        {
            throw new TimeoutException("Los shaders no estuvieron listos para las miniaturas de plantillas.");
        }

        foreach (MotionTemplateEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind != TemplateKind.Title)
            {
                continue;
            }

            try
            {
                done(entry, RenderOne(entry, gpu, titles, compositor, cancellationToken));
            }
            catch (Exception ex) when (ex is PackageContentException or Rodaje.Media.Export.ExportException or TimeoutException)
            {
                // Una plantilla que no se puede componer se queda con su ícono; las demás siguen.
            }
        }
    }

    private static TemplateStrip RenderOne(MotionTemplateEntry entry, GpuDevice gpu, TitleRasterizer titles, Compositor compositor, CancellationToken cancellationToken)
    {
        Project project = BuildProject(entry);
        var options = new OfflineTimelineOptions { Titles = titles, MissingContent = MissingContentPolicy.ContinueWithout, LookaheadFrames = 1, MaxDecodersPerMedia = 1 };
        using OfflineTimeline timeline = OfflineTimeline.Open(project, SequenceId, gpu, options);
        var strip = new byte[Width * Frames * Height * 4];
        long deadline = Environment.TickCount64 + 10_000;
        for (int i = 0; i < Frames; i++)
        {
            long frame = Math.Min(timeline.FrameCount - 1, (long)((i + 0.5) / Frames * timeline.FrameCount));
            CompositeResult result;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("Los títulos de la miniatura no llegaron a tiempo.");
                }

                using CompositionFrame layers = timeline.Render(frame, cancellationToken);
                result = compositor.Render(layers);
                if (result.TitlesPending > 0)
                {
                    Thread.Sleep(2);
                }
            }
            while (result.TitlesPending > 0);

            byte[] rgba = compositor.ReadbackRgba();
            for (int y = 0; y < Height; y++)
            {
                rgba.AsSpan(y * Width * 4, Width * 4).CopyTo(strip.AsSpan((y * Width * Frames * 4) + (i * Width * 4)));
            }
        }

        return new TemplateStrip(Width, Height, Frames, strip);
    }

    /// <summary>PNG RGBA de 8 bits sin filtro por fila: lo justo para guardar y volver a leer la tira.</summary>
    public static byte[] EncodePng(TemplateStrip strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        int width = strip.FrameWidth * strip.Frames;
        int stride = width * 4;
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < strip.FrameHeight; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(strip.Rgba, y * stride, stride);
            }
        }

        using var png = new MemoryStream();
        png.Write(PngSignature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], strip.FrameHeight);
        header[8] = 8;
        header[9] = 6;
        header[10] = header[11] = header[12] = 0;
        WriteChunk(png, "IHDR", header);
        // Los cuadros de la tira van en un fragmento de texto: quien la lea no tiene que suponer que son 12.
        WriteChunk(png, "tEXt", Encoding.ASCII.GetBytes(FramesKey + strip.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        WriteChunk(png, "IDAT", data.GetBuffer().AsSpan(0, (int)data.Length));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>La tira de un PNG escrito por <see cref="EncodePng"/>; nula con cualquier otro archivo.</summary>
    public static TemplateStrip? DecodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length < PngSignature.Length || !png[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return null;
        }

        int width = 0, height = 0, frames = 0;
        using var data = new MemoryStream();
        for (int at = PngSignature.Length; at + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[at..]);
            if (length < 0 || at + 12 + (long)length > png.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> type = png.Slice(at + 4, 4);
            ReadOnlySpan<byte> body = png.Slice(at + 8, length);
            if (BinaryPrimitives.ReadUInt32BigEndian(png[(at + 8 + length)..]) != Crc(type, body))
            {
                return null;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (length != 13 || body[8] != 8 || body[9] != 6 || body[12] != 0)
                {
                    return null;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
            }
            else if (type.SequenceEqual("tEXt"u8) && body.StartsWith(Encoding.ASCII.GetBytes(FramesKey)))
            {
                _ = int.TryParse(body[FramesKey.Length..], System.Globalization.CultureInfo.InvariantCulture, out frames);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data.Write(body);
            }

            at += 12 + length;
        }

        if (frames is < 1 or > 64 || height is < 1 or > 1024 || width < frames || width > 16384 || width % frames != 0)
        {
            return null;
        }

        int stride = width * 4;
        var rgba = new byte[stride * height];
        data.Position = 0;
        try
        {
            using var zlib = new ZLibStream(data, CompressionMode.Decompress);
            for (int y = 0; y < height; y++)
            {
                if (zlib.ReadByte() != 0)
                {
                    return null;
                }

                zlib.ReadExactly(rgba, y * stride, stride);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }

        return new TemplateStrip(width / frames, height, frames, rgba);
    }

    private static void WriteChunk(Stream png, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> word = stackalloc byte[4];
        Span<byte> name = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, name);
        BinaryPrimitives.WriteInt32BigEndian(word, body.Length);
        png.Write(word);
        png.Write(name);
        png.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(name, body));
        png.Write(word);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in body)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
