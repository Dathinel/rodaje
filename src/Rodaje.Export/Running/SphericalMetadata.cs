using System.Buffers.Binary;
using System.Text;

namespace Rodaje.Export.Running;

/// <summary>
/// Metadatos de video 360 (Spherical Video V2: cajas <c>st3d</c> y <c>sv3d</c> con proyección equirectangular) en la entrada
/// de muestra del primer video de un MP4 o MOV ya escrito. FFmpeg no los escribe desde la línea de comandos, así que se
/// añaden después: se reescribe el <c>moov</c> con las cajas nuevas, se corrigen los tamaños de sus contenedores y, si el
/// <c>moov</c> va antes de los datos (<c>+faststart</c>), los desplazamientos de <c>stco</c>/<c>co64</c>.
/// </summary>
public static class SphericalMetadata
{
    private const int VisualSampleEntryHeader = 86;
    private const int StsdHeader = 16;
    private const string Source = "Rodaje";

    /// <summary>Añade los metadatos al archivo. No hace nada si ya los tiene.</summary>
    /// <exception cref="InvalidDataException">El archivo no es un MP4/MOV con video que se pueda editar.</exception>
    public static void Inject(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        (long moovOffset, long moovSize, long firstMdat, long length) = TopLevel(path);
        byte[] moov = new byte[moovSize];
        using (FileStream read = File.OpenRead(path))
        {
            read.Position = moovOffset;
            read.ReadExactly(moov);
        }

        if (HasSpherical(moov))
        {
            return;
        }

        byte[] updated = InjectIntoMoov(moov);
        long delta = updated.Length - moov.Length;
        if (moovOffset + moovSize == length)
        {
            // moov al final: se reescribe en su sitio, los datos no se mueven.
            using FileStream write = new(path, FileMode.Open, FileAccess.Write, FileShare.None);
            write.SetLength(moovOffset);
            write.Position = moovOffset;
            write.Write(updated);
            return;
        }

        if (firstMdat > moovOffset)
        {
            ShiftChunkOffsets(updated, delta);
        }

        string temporary = path + ".360-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (FileStream source = File.OpenRead(path))
            using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                Copy(source, target, moovOffset);
                target.Write(updated);
                source.Position = moovOffset + moovSize;
                source.CopyTo(target);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Si el primer video del archivo declara proyección equirectangular.</summary>
    public static bool IsEquirectangular(string path)
    {
        (long moovOffset, long moovSize, _, _) = TopLevel(path);
        byte[] moov = new byte[moovSize];
        using FileStream read = File.OpenRead(path);
        read.Position = moovOffset;
        read.ReadExactly(moov);
        return HasSpherical(moov);
    }

    /// <summary>El <c>moov</c> con <c>st3d</c> (mono) y <c>sv3d</c> (equirectangular sin recorte) al final de la entrada de muestra del video.</summary>
    internal static byte[] InjectIntoMoov(byte[] moov)
    {
        (List<int> chain, int entry) = VideoSampleEntry(moov)
            ?? throw new InvalidDataException("El archivo no tiene una pista de video con su entrada de muestra.");
        byte[] boxes = SphericalBoxes();
        int entryEnd = entry + (int)BoxSize(moov, entry);
        byte[] result = new byte[moov.Length + boxes.Length];
        moov.AsSpan(0, entryEnd).CopyTo(result);
        boxes.CopyTo(result, entryEnd);
        moov.AsSpan(entryEnd).CopyTo(result.AsSpan(entryEnd + boxes.Length));
        foreach (int box in chain.Append(entry))
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(box));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(box), checked(size + (uint)boxes.Length));
        }

        return result;
    }

    private static byte[] SphericalBoxes()
    {
        byte[] source = [.. Encoding.ASCII.GetBytes(Source), 0];
        byte[] svhd = Box("svhd", [0, 0, 0, 0, .. source]);
        byte[] prhd = Box("prhd", new byte[4 + 12]);
        byte[] equi = Box("equi", new byte[4 + 16]);
        byte[] proj = Box("proj", [.. prhd, .. equi]);
        byte[] sv3d = Box("sv3d", [.. svhd, .. proj]);
        byte[] st3d = Box("st3d", [0, 0, 0, 0, 0]);
        return [.. st3d, .. sv3d];
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    private static bool HasSpherical(byte[] moov) =>
        VideoSampleEntry(moov) is { } found && Child(moov, found.Entry + VisualSampleEntryHeader, found.Entry + (int)BoxSize(moov, found.Entry), "sv3d") is { } sv3d
        && Child(moov, sv3d + 8, sv3d + (int)BoxSize(moov, sv3d), "proj") is { } proj
        && Child(moov, proj + 8, proj + (int)BoxSize(moov, proj), "equi") is not null;

    /// <summary>La cadena moov → trak → mdia → minf → stbl → stsd del primer video y el offset de su primera entrada.</summary>
    private static (List<int> Chain, int Entry)? VideoSampleEntry(byte[] moov)
    {
        if (moov.Length < 8 || Type(moov, 0) != "moov")
        {
            return null;
        }

        foreach (int trak in Children(moov, 8, moov.Length, "trak"))
        {
            int trakEnd = trak + (int)BoxSize(moov, trak);
            if (Child(moov, trak + 8, trakEnd, "mdia") is not { } mdia)
            {
                continue;
            }

            int mdiaEnd = mdia + (int)BoxSize(moov, mdia);
            if (Child(moov, mdia + 8, mdiaEnd, "hdlr") is not { } hdlr || hdlr + 20 > moov.Length || Encoding.ASCII.GetString(moov, hdlr + 16, 4) != "vide")
            {
                continue;
            }

            if (Child(moov, mdia + 8, mdiaEnd, "minf") is { } minf
                && Child(moov, minf + 8, minf + (int)BoxSize(moov, minf), "stbl") is { } stbl
                && Child(moov, stbl + 8, stbl + (int)BoxSize(moov, stbl), "stsd") is { } stsd
                && stsd + StsdHeader + 8 <= stsd + BoxSize(moov, stsd)
                && BoxSize(moov, stsd + StsdHeader) >= VisualSampleEntryHeader)
            {
                return ([0, trak, mdia, minf, stbl, stsd], stsd + StsdHeader);
            }
        }

        return null;
    }

    private static void ShiftChunkOffsets(byte[] moov, long delta)
    {
        foreach (int trak in Children(moov, 8, moov.Length, "trak"))
        {
            if (Child(moov, trak + 8, trak + (int)BoxSize(moov, trak), "mdia") is not { } mdia
                || Child(moov, mdia + 8, mdia + (int)BoxSize(moov, mdia), "minf") is not { } minf
                || Child(moov, minf + 8, minf + (int)BoxSize(moov, minf), "stbl") is not { } stbl)
            {
                continue;
            }

            int stblEnd = stbl + (int)BoxSize(moov, stbl);
            if (Child(moov, stbl + 8, stblEnd, "stco") is { } stco)
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stco + 12));
                for (int i = 0; i < count; i++)
                {
                    Span<byte> at = moov.AsSpan(stco + 16 + (i * 4), 4);
                    long value = BinaryPrimitives.ReadUInt32BigEndian(at) + delta;
                    if (value > uint.MaxValue)
                    {
                        throw new InvalidDataException("El archivo es demasiado grande para mover sus datos con stco de 32 bits.");
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(at, (uint)value);
                }
            }
            else if (Child(moov, stbl + 8, stblEnd, "co64") is { } co64)
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(co64 + 12));
                for (int i = 0; i < count; i++)
                {
                    Span<byte> at = moov.AsSpan(co64 + 16 + (i * 8), 8);
                    BinaryPrimitives.WriteUInt64BigEndian(at, (ulong)((long)BinaryPrimitives.ReadUInt64BigEndian(at) + delta));
                }
            }
        }
    }

    /// <summary>Dónde está el <c>moov</c>, dónde empieza el primer <c>mdat</c> y el largo del archivo.</summary>
    private static (long MoovOffset, long MoovSize, long FirstMdat, long Length) TopLevel(string path)
    {
        using FileStream stream = File.OpenRead(path);
        long length = stream.Length;
        long offset = 0;
        long moov = -1, moovSize = 0, mdat = long.MaxValue;
        Span<byte> header = stackalloc byte[16];
        while (offset + 8 <= length)
        {
            stream.Position = offset;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header[4..8]);
            if (size == 1)
            {
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }
            else if (size == 0)
            {
                size = length - offset;
            }

            if (size < 8 || offset + size > length)
            {
                throw new InvalidDataException("El archivo tiene una caja de nivel superior con un tamaño imposible.");
            }

            if (type == "moov")
            {
                if (size > 256L * 1024 * 1024)
                {
                    throw new InvalidDataException("El moov es demasiado grande.");
                }

                (moov, moovSize) = (offset, size);
            }
            else if (type == "mdat")
            {
                mdat = Math.Min(mdat, offset);
            }

            offset += size;
        }

        return moov < 0 ? throw new InvalidDataException("El archivo no es un MP4 ni un MOV (no tiene moov).") : (moov, moovSize, mdat, length);
    }

    private static void Copy(Stream source, Stream target, long count)
    {
        byte[] buffer = new byte[1 << 20];
        while (count > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static IEnumerable<int> Children(byte[] data, int start, int end, string type)
    {
        int at = start;
        while (at + 8 <= end)
        {
            long size = BoxSize(data, at);
            if (size < 8 || at + size > end)
            {
                yield break;
            }

            if (Type(data, at) == type)
            {
                yield return at;
            }

            at += (int)size;
        }
    }

    private static int? Child(byte[] data, int start, int end, string type)
    {
        foreach (int found in Children(data, start, end, type))
        {
            return found;
        }

        return null;
    }

    private static long BoxSize(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

    private static string Type(byte[] data, int at) => Encoding.ASCII.GetString(data, at + 4, 4);
}
