using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;

namespace Rodaje.Render;

/// <summary>Mide en la GPU el tiempo entre <see cref="Begin"/> y <see cref="End"/> con consultas de marca de tiempo.</summary>
/// <remarks>
/// Mide lo que la GPU ejecuta entre las dos marcas, incluido el trabajo que otros hilos (el decodificador) metan
/// en la cola del mismo contexto entre medias. Un intervalo "disjunto" (cambio de reloj de la GPU) no da medida.
/// </remarks>
public sealed class GpuTimer : IDisposable
{
    private readonly D3D11Gpu _engine;
    private readonly GpuTimerQuery _timer;

    public GpuTimer(GpuDevice gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _engine = D3D11Gpu.For(gpu);
        _timer = _engine.CreateTimer();
    }

    public void Begin()
    {
        using IGpuFrame frame = _engine.BeginFrame();
        frame.BeginTimer(_timer);
    }

    public void End()
    {
        using IGpuFrame frame = _engine.BeginFrame();
        frame.EndTimer(_timer);
    }

    /// <summary>Espera el resultado; devuelve <c>null</c> si el intervalo fue disjunto o no llegó a tiempo.</summary>
    public double? WaitMilliseconds(TimeSpan timeout) => _timer.WaitMilliseconds(timeout);

    public void Dispose() => _timer.Dispose();
}
