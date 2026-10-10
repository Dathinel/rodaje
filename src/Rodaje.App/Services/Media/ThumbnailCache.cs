using System.Buffers.Binary;
using System.Globalization;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

/// <summary>Una miniatura en memoria: el archivo (ruta absoluta, sin distinguir mayúsculas), el instante en ticks y el alto.</summary>
public readonly record struct ThumbnailKey(string Path, long TimeTicks, int Height)
{
    public bool Equals(ThumbnailKey other) =>
        TimeTicks == other.TimeTicks && Height == other.Height && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), TimeTicks, Height);
}

/// <summary>Caché en memoria de miniaturas, con presupuesto en bytes y expulsión de la menos usada. Segura entre hilos.</summary>
public sealed class ThumbnailMemoryCache(long budgetBytes)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ThumbnailKey, LinkedListNode<(ThumbnailKey Key, BgraImage Image)>> _map = [];
    private readonly LinkedList<(ThumbnailKey Key, BgraImage Image)> _order = new();
    private long _bytes;

    public long BudgetBytes { get; } = budgetBytes;

    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    public bool TryGet(ThumbnailKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BgraImage? image)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    public bool Contains(ThumbnailKey key)
    {
        lock (_gate)
        {
            return _map.ContainsKey(key);
        }
    }

    public void Add(ThumbnailKey key, BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
        {
            if (_map.Remove(key, out var old))
            {
                _order.Remove(old);
                _bytes -= old.Value.Image.SizeBytes;
            }

            _map[key] = _order.AddFirst((key, image));
            _bytes += image.SizeBytes;
            while (_bytes > BudgetBytes && _order.Last is { } last && !ReferenceEquals(last.Value.Image, image))
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Image.SizeBytes;
            }
        }
    }

    /// <summary>Suelta todas las miniaturas de un archivo.</summary>
    public void RemoveFile(string path)
    {
        lock (_gate)
        {
            for (LinkedListNode<(ThumbnailKey Key, BgraImage Image)>? node = _order.First; node is not null;)
            {
                LinkedListNode<(ThumbnailKey Key, BgraImage Image)>? next = node.Next;
                if (string.Equals(node.Value.Key.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    _order.Remove(node);
                    _map.Remove(node.Value.Key);
                    _bytes -= node.Value.Image.SizeBytes;
                }

                node = next;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }
}

/// <summary>
/// Miniaturas en disco, bajo <c>cache\miniaturas</c>: un archivo por miniatura, nombrado por la huella del original (así
/// un archivo que cambia no reutiliza miniaturas viejas), el alto y el instante. Encabezado de 16 bytes y BGRA crudo.
/// </summary>
public sealed class ThumbnailDiskCache(string directory)
{
    private const uint Magic = 0x4D4A4452; // "RDJM"
    private const ushort Version = 1;
    private const int HeaderBytes = 16;

    public string Directory { get; } = directory;

    /// <summary>Nombre del archivo de una miniatura: <c>{huella}-{alto}-{ticks}.miniatura</c> dentro de una subcarpeta por huella.</summary>
    public static string FileName(string sourceKey, int height, long timeTicks) =>
        string.Create(CultureInfo.InvariantCulture, $"{sourceKey}-{height}-{timeTicks}.miniatura");

    public string PathOf(string sourceKey, int height, long timeTicks) =>
        System.IO.Path.Combine(Directory, sourceKey[..Math.Min(2, sourceKey.Length)], FileName(sourceKey, height, timeTicks));

    public BgraImage? TryRead(string sourceKey, int height, long timeTicks)
    {
        string path = PathOf(sourceKey, height, timeTicks);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(path);
            return Decode(data);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string sourceKey, int height, long timeTicks, BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string path = PathOf(sourceKey, height, timeTicks);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, Encode(image));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Borra las más viejas hasta quedar por debajo de <paramref name="maxBytes"/>; devuelve cuántas borró.</summary>
    public int Trim(long maxBytes) => DiskCacheTrimmer.Trim(Directory, "*.miniatura", maxBytes);

    internal static byte[] Encode(BgraImage image)
    {
        byte[] data = new byte[HeaderBytes + image.Pixels.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), image.Height);
        image.Pixels.CopyTo(data, HeaderBytes);
        return data;
    }

    internal static BgraImage? Decode(byte[] data)
    {
        if (data.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != Version)
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096 || data.Length != HeaderBytes + ((long)width * height * 4))
        {
            return null;
        }

        return new BgraImage(width, height, data[HeaderBytes..]);
    }
}

/// <summary>Recorta una carpeta de caché borrando primero lo que hace más tiempo no se escribe.</summary>
internal static class DiskCacheTrimmer
{
    public static int Trim(string directory, string pattern, long maxBytes)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        long total = files.Sum(f => f.Length);
        int removed = 0;
        // Se baja al 80 % para no recortar en cada arranque.
        long goal = maxBytes * 8 / 10;
        if (total <= maxBytes)
        {
            return 0;
        }

        foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= goal)
            {
                break;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
