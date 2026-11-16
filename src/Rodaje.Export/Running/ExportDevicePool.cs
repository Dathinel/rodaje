using Rodaje.Media.Gpu;

namespace Rodaje.Export.Running;

/// <summary>Un dispositivo de fondo por propietario; el último trabajo lo libera si ya se cerró el propietario.</summary>
public sealed class ExportDevicePool : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<GpuDevice> _create;
    private GpuDevice? _device;
    private int _users;
    private bool _disposed;

    public ExportDevicePool(Func<GpuDevice>? create = null) => _create = create ?? GpuDevice.Create;

    internal Lease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _device ??= _create();
            _users++;
            return new Lease(this, _device);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_users == 0)
            {
                _device?.Dispose();
                _device = null;
            }
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _users--;
            if (_disposed && _users == 0)
            {
                _device?.Dispose();
                _device = null;
            }
        }
    }

    internal sealed class Lease(ExportDevicePool owner, GpuDevice device) : IDisposable
    {
        private ExportDevicePool? _owner = owner;

        public GpuDevice Device { get; } = device;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
