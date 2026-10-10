using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Mide en la GPU el tiempo entre <see cref="Begin"/> y <see cref="End"/> con consultas de marca de tiempo de D3D11.</summary>
/// <remarks>
/// Mide lo que la GPU ejecuta entre las dos marcas, incluido el trabajo que otros hilos (el decodificador) metan
/// en la cola del mismo contexto entre medias. Un intervalo "disjunto" (cambio de reloj de la GPU) no da medida.
/// </remarks>
public sealed unsafe class GpuTimer : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11DeviceContext* _context;
    private ID3D11Query* _disjoint;
    private ID3D11Query* _start;
    private ID3D11Query* _end;

    public GpuTimer(GpuDevice gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        var device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        try
        {
            _disjoint = CreateQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT);
            _start = CreateQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
            _end = CreateQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Begin()
    {
        _gpu.LockContext();
        try
        {
            _context->Begin((ID3D11Asynchronous*)_disjoint);
            _context->End((ID3D11Asynchronous*)_start);
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    public void End()
    {
        _gpu.LockContext();
        try
        {
            _context->End((ID3D11Asynchronous*)_end);
            _context->End((ID3D11Asynchronous*)_disjoint);
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    /// <summary>Espera el resultado; devuelve <c>null</c> si el intervalo fue disjunto o no llegó a tiempo.</summary>
    public double? WaitMilliseconds(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint;
        ulong start;
        ulong end;
        bool flushed = false;
        while (true)
        {
            _gpu.LockContext();
            try
            {
                uint flags = flushed ? (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH : 0;
                flushed = true;
                if (Ready(_context->GetData((ID3D11Asynchronous*)_disjoint, &disjoint, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), flags))
                    && Ready(_context->GetData((ID3D11Asynchronous*)_start, &start, sizeof(ulong), flags))
                    && Ready(_context->GetData((ID3D11Asynchronous*)_end, &end, sizeof(ulong), flags)))
                {
                    break;
                }
            }
            finally
            {
                _gpu.UnlockContext();
            }

            if (Environment.TickCount64 > deadline)
            {
                return null;
            }

            Thread.Yield();
        }

        return disjoint.Disjoint || disjoint.Frequency == 0 ? null : (end - start) * 1000.0 / disjoint.Frequency;
    }

    public void Dispose()
    {
        Com.Release(ref _disjoint);
        Com.Release(ref _start);
        Com.Release(ref _end);
    }

    private static bool Ready(HRESULT hr)
    {
        Check(hr, "GetData");
        return hr == S.S_OK;
    }

    private static ID3D11Query* CreateQuery(ID3D11Device* device, D3D11_QUERY kind)
    {
        var desc = new D3D11_QUERY_DESC { Query = kind };
        ID3D11Query* query;
        Check(device->CreateQuery(&desc, &query), "CreateQuery");
        return query;
    }
}
