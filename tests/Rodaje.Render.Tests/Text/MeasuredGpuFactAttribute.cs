using System.Diagnostics;
using Rodaje.Media.Gpu;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests.Text;

/// <summary>
/// Una medida de tiempo de GPU: necesita lo mismo que <see cref="GpuFactAttribute"/> y, además, que el adaptador sea una
/// GPU de verdad. En un equipo sin GPU (como el runner de Windows de la CI) Direct3D 11 «por hardware» da el
/// Microsoft Basic Render Driver, que rasteriza en la CPU con WARP: lo que mide la consulta de tiempo es CPU compartida
/// con el resto de las pruebas y no dice nada del costo en una GPU.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MeasuredGpuFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SoftwareAdapterReason = new(() =>
    {
        using GpuDevice? gpu = GpuDevice.TryCreate(out _);
        return gpu is not null && gpu.IsSoftware
            ? $"Omitida: el adaptador es {gpu.AdapterName} (WARP, rasterizado en la CPU, sin GPU); el tiempo de GPU no se puede medir."
            : null;
    });

    public MeasuredGpuFactAttribute() =>
        Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.GpuTextures, SoftwareAdapterReason.Value);

    /// <summary>
    /// Si una prueba que también comprueba otra cosa puede exigir sus cotas de tiempo: con código optimizado y una GPU de
    /// verdad. En Debug o con WARP el tiempo solo se informa.
    /// </summary>
    internal static bool CanTime(GpuDevice gpu) =>
        !gpu.IsSoftware && typeof(Compositor).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
}
