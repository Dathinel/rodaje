using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace Rodaje.Modules.Protocol;

/// <summary>
/// Memoria compartida con nombre para pasar cuadros y audio sin copiarlos por la tubería. La crea siempre el editor
/// (<see cref="Create"/>); el módulo la abre por nombre (<see cref="Open"/>) y solo escribe en los búferes de la llamada.
/// Todo acceso comprueba los límites.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SharedMemoryRegion : IDisposable
{
    public const string NamePrefix = "Rodaje.Mod.";

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;

    private SharedMemoryRegion(string name, MemoryMappedFile file, long capacity)
    {
        Name = name;
        _file = file;
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
        return new SharedMemoryRegion(name, MemoryMappedFile.CreateNew(@"Local\" + name, capacity), capacity);
    }

    /// <exception cref="ModuleCallException">Si el nombre no es válido o la región no existe.</exception>
    public static SharedMemoryRegion Open(string name)
    {
        if (!IsValidName(name)) throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "Nombre de región inválido.");
        try
        {
            return new SharedMemoryRegion(name, MemoryMappedFile.OpenExisting(@"Local\" + name, MemoryMappedFileRights.ReadWrite), 0);
        }
        catch (FileNotFoundException ex)
        {
            throw new ModuleCallException(ErrorCodes.BufferOutOfRange, "La región no existe: " + ex.Message);
        }
    }

    public static bool IsValidName(string? name) => Limits.RegionName(name);

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
    }
}
