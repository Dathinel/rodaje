using Xunit;

namespace Rodaje.Engine.Tests.Recording;

/// <summary>
/// Prueba que escribe en <c>/dev/full</c>, el dispositivo que acepta abrirse pero falla cada escritura con «no queda
/// espacio» (ENOSPC): un disco lleno de verdad, con el error que da el sistema y no uno inyectado. Donde no existe se
/// informa como omitida, con la causa.
/// </summary>
internal sealed class DevFullFactAttribute : FactAttribute
{
    public const string Device = "/dev/full";

    public DevFullFactAttribute()
    {
        if (!File.Exists(Device))
        {
            Skip = "/dev/full solo existe en Linux";
        }
    }
}
