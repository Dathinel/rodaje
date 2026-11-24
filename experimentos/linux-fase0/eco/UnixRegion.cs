using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Rodaje.Modules.Protocol;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// Región de memoria compartida en Unix, equivalente a <c>SharedMemoryRegion</c> de Windows: el nombre del
/// protocolo (<c>Rodaje.Mod.&lt;hex&gt;</c>) se resuelve a un archivo en la carpeta 0700 de la ejecución y se
/// mapea con <see cref="MemoryMappedFile"/> (que usa mmap por dentro: no hace falta el P/Invoke ni sus
/// constantes). La creación es exclusiva (FileMode.CreateNew es O_CREAT|O_EXCL, que también rechaza un
/// enlace en el nombre) con permisos 0600 fijados en el mismo open por UnixCreateMode — open() con modo
/// es variádica y en arm64 de Apple ese argumento va por la pila, no por un registro, así que no se puede
/// llamar por P/Invoke —; al abrir se usa O_NOFOLLOW y se comprueba con fstat que es un archivo regular,
/// del mismo usuario, con permisos 600 y con el tamaño mínimo esperado. El archivo se borra al cerrar.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed unsafe class UnixRegion : IDisposable
{
    public const string NamePrefix = "Rodaje.Mod.";

    private readonly string _path;
    private readonly bool _mine;
    private readonly FileStream? _stream;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;

    private UnixRegion(string name, string path, bool mine, FileStream? stream, SafeFileHandle? handle, long capacity)
    {
        Name = name;
        _path = path;
        _mine = mine;
        _stream = stream;
        try
        {
            // En Unix el nombre del mapeo no existe: el valor compartido es el archivo. Con leaveOpen: false
            // (el abierto por fd) el descriptor se cierra con el mapeo; creado, lo cierra _stream.
            _file = handle is null
                ? MemoryMappedFile.CreateFromFile(stream!.SafeFileHandle, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true)
                : MemoryMappedFile.CreateFromFile(handle, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            _view = _file.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
        }
        catch
        {
            _view?.Dispose();
            _file?.Dispose();
            stream?.Dispose();
            handle?.Dispose();
            throw;
        }
        Capacity = capacity;
    }

    public string Name { get; }

    public long Capacity { get; }

    public bool IsDisposed { get; private set; }

    /// <summary>Crea una región nueva con nombre aleatorio.</summary>
    public static UnixRegion Create(string dir, long capacity) => Create(dir, NamePrefix + Guid.NewGuid().ToString("N"), capacity);

    /// <summary>Creación exclusiva (FileMode.CreateNew, modo 600): falla si el nombre ya existe o es un enlace.</summary>
    public static UnixRegion Create(string dir, string name, long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if (!Limits.RegionName(name)) throw new ArgumentException("Nombre de región inválido.", nameof(name));
        string path = Path.Combine(dir, name);
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew, // O_CREAT|O_EXCL: un enlace simbólico en el nombre da EEXIST.
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, // 0600
        };
        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo crear «{name}».", ex);
        }
        try
        {
            stream.SetLength(capacity); // ftruncate, por la API de .NET.
            return new UnixRegion(name, path, mine: true, stream, handle: null, capacity);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Abre una región creada por el editor, sin seguir enlaces (O_NOFOLLOW) y comprobando con fstat que es un
    /// archivo regular del mismo usuario, con permisos 600 y de al menos <paramref name="minCapacity"/> bytes.
    /// </summary>
    /// <exception cref="ModuleCallException">Nombre inválido, no existe, o no pasa las comprobaciones.</exception>
    public static UnixRegion Open(string dir, string name, long minCapacity = 1)
    {
        if (!Limits.RegionName(name)) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "Nombre de región inválido.");
        string path = Path.Combine(dir, name);
        int fd = Posix.Open(path, Posix.O_RDWR | Posix.O_NOFOLLOW | Posix.O_CLOEXEC);
        if (fd < 0)
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"La región no se puede abrir (errno {Posix.Errno()}).");
        SafeFileHandle handle = new(fd, ownsHandle: true);
        try
        {
            CheckFile(handle, minCapacity, out long size);
            return new UnixRegion(name, path, mine: false, stream: null, handle, size);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Comprueba dueño, tipo y permisos con fstat y el tamaño con la API de .NET.</summary>
    private static void CheckFile(SafeFileHandle handle, long minCapacity, out long size)
    {
        int fd = (int)handle.DangerousGetHandle();
        Span<byte> buf = stackalloc byte[Posix.STAT_BUF];
        fixed (byte* p = buf)
        {
            if (Posix.FStat(fd, p) != 0)
                throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"No se pudo inspeccionar la región (fstat, errno {Posix.Errno()}).");
        }
        bool mac = OperatingSystem.IsMacOS();
        int modeOff = mac ? Posix.STAT_MODE_MAC : Posix.STAT_MODE_LINUX;
        uint mode = mac ? BitConverter.ToUInt16(buf[modeOff..]) : BitConverter.ToUInt32(buf[modeOff..]);
        uint uid = BitConverter.ToUInt32(buf[(mac ? Posix.STAT_UID_MAC : Posix.STAT_UID_LINUX)..]);
        if ((mode & Posix.S_IFMT) != Posix.S_IFREG)
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región no es un archivo regular.");
        if (uid != Posix.GetEuid())
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región es de otro usuario.");
        if ((mode & 0x1FF /* 0777 */) != 0x180 /* 0600 */)
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"La región tiene permisos {Convert.ToString(mode & 0x1FF, 8)} en vez de 600.");
        size = RandomAccess.GetLength(handle);
        if (size < minCapacity)
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"La región mide {size} bytes; se esperaban al menos {minCapacity}.");
    }

    /// <exception cref="ModuleCallException">Si el rango se sale de la región.</exception>
    public Span<byte> GetSpan(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (offset < 0 || length < 0 || length > int.MaxValue || offset > Capacity - length)
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"Búfer {offset}+{length} fuera de la región de {Capacity} bytes.");
        return new Span<byte>(_pointer + offset, (int)length);
    }

    public Span<byte> GetSpan(SharedBufferRef buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Region != Name) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "El búfer es de otra región.");
        return GetSpan(buffer.Offset, buffer.Length);
    }

    public SharedBufferRef Describe(long offset, long length, VideoLayout? video = null, AudioLayout? audio = null)
    {
        GetSpan(offset, length);
        SharedBufferRef buffer = new(Name, offset, length, video, audio);
        if (buffer.Validate() is { } reason) throw new ArgumentException(reason);
        return buffer;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
        _view.Dispose();
        _file.Dispose();
        _stream?.Dispose();
        if (_mine)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ya no está.
            }
        }
    }
}
