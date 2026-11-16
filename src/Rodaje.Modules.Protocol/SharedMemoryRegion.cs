using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Rodaje.Modules.Protocol;

/// <summary>
/// Memoria compartida con nombre para pasar cuadros y audio sin copiarlos por la tubería. La crea siempre el editor
/// (<see cref="Create"/>); el módulo la abre por nombre (<see cref="Open(string)"/>) y solo escribe en los búferes de la
/// llamada. Todo acceso comprueba los límites.
/// </summary>
/// <remarks>
/// En Windows es un mapeo con nombre en <c>Local\</c>. En Linux y macOS el nombre se resuelve a un archivo de la carpeta
/// 0700 de la sesión del editor (<see cref="UnixRuntime"/>): se crea en exclusiva con permisos 0600, al abrirlo se rechaza
/// un enlace, otro tipo de archivo o permisos distintos de 0600, y se borra al cerrarlo el editor. El nombre que viaja por
/// el protocolo es el mismo en los tres sistemas.
/// </remarks>
public sealed unsafe class SharedMemoryRegion : IDisposable
{
    public const string NamePrefix = "Rodaje.Mod.";

    private const UnixFileMode Mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly string? _ownedPath;
    private byte* _pointer;

    private SharedMemoryRegion(string name, MemoryMappedFile file, long capacity, string? ownedPath = null)
    {
        Name = name;
        _file = file;
        _ownedPath = ownedPath;
        _view = file.CreateViewAccessor(0, capacity);
        Capacity = capacity == 0 ? _view.Capacity : capacity;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        _pointer += _view.PointerOffset;
    }

    public string Name { get; }

    public long Capacity { get; }

    public bool IsDisposed { get; private set; }

    public static SharedMemoryRegion Create(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        string name = NamePrefix + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
        {
            return new SharedMemoryRegion(name, MemoryMappedFile.CreateNew(@"Local\" + name, capacity), capacity);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return CreateFile(UnixRuntime.SessionDirectory, name, capacity);
        }

        throw new PlatformNotSupportedException("La memoria compartida de los módulos solo existe en Windows, Linux y macOS.");
    }

    /// <exception cref="ModuleCallException">Si el nombre no es válido o la región no existe.</exception>
    public static SharedMemoryRegion Open(string name) => Open(name, null);

    /// <summary>Abre la región; en Linux y macOS, en <paramref name="directory"/> (la carpeta del socket del editor).</summary>
    /// <exception cref="ModuleCallException">Si el nombre no es válido, la región no existe o no pasa las comprobaciones.</exception>
    public static SharedMemoryRegion Open(string name, string? directory)
    {
        if (!IsValidName(name)) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "Nombre de región inválido.");
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new SharedMemoryRegion(name, MemoryMappedFile.OpenExisting(@"Local\" + name, MemoryMappedFileRights.ReadWrite), 0);
            }
            catch (FileNotFoundException ex)
            {
                throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región no existe: " + ex.Message);
            }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return OpenFile(directory ?? UnixRuntime.SessionDirectory, name);
        }

        throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La memoria compartida no existe en este sistema.");
    }

    public static bool IsValidName(string? name) => Limits.RegionName(name);

    // FileMode.CreateNew es O_CREAT|O_EXCL: falla si el nombre existe, también si es un enlace. El modo 0600 va en el
    // mismo open (UnixCreateMode); open con modo es variádica y por P/Invoke llegaría mal en arm64 de Apple.
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static SharedMemoryRegion CreateFile(string directory, string name, long capacity)
    {
        string path = Path.Combine(directory, name);
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite, // en Unix .NET bloquea con flock: el módulo tiene que poder abrirla.
            UnixCreateMode = Mode0600,
        });
        try
        {
            stream.SetLength(capacity);
            MemoryMappedFile file = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: false);
            return new SharedMemoryRegion(name, file, capacity, path);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static SharedMemoryRegion OpenFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        FileStream stream;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región no existe.");
            if (info.LinkTarget is not null) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región es un enlace.");
            if (info.UnixFileMode != Mode0600)
                throw new ModuleCallException(ErrorCodes.BufferOutOfRange, $"La región tiene permisos {Convert.ToString((int)info.UnixFileMode, 8)} en vez de 600.");
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región no se puede abrir: " + ex.Message);
        }

        try
        {
            // Entre la comprobación y el open el nombre pudo cambiar: se mira otra vez lo abierto, sin seguir el nombre.
            if (new FileInfo(path).LinkTarget is not null || stream.Length < 1)
                throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región cambió al abrirla o está vacía.");
            long capacity = stream.Length;
            MemoryMappedFile file = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, leaveOpen: false);
            return new SharedMemoryRegion(name, file, capacity);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
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
        if (_ownedPath is not null)
        {
            try
            {
                File.Delete(_ownedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo barre el siguiente editor con la carpeta de esta sesión.
            }
        }
    }
}
