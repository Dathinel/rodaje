using Rodaje.Export.Running;
using Rodaje.Media.Gpu;
using Xunit;

namespace Rodaje.Export.Tests.Running;

[Collection(ExportGpuGroup.Name)]
// El pool presta dispositivos D3D11.
[Trait("Categoria", "SoloWindows")]
public sealed class ExportDevicePoolTests
{
    [Fact]
    public void Un_dispositivo_perdido_se_cambia_por_otro_en_el_siguiente_trabajo()
    {
        var created = new List<GpuDevice>();
        using var devices = new ExportDevicePool(() =>
        {
            GpuDevice device = GpuDevice.CreateWarp();
            created.Add(device);
            return device;
        });
        GpuDevice first;
        using (ExportDevicePool.Lease lease = devices.Acquire())
        {
            first = lease.Device;
        }

        using (ExportDevicePool.Lease again = devices.Acquire())
        {
            Assert.Same(first, again.Device);
        }

        // Cerrado desde fuera cuenta como perdido: sin él, cada fragmento de fondo fallaría hasta reiniciar la app.
        first.Dispose();
        using ExportDevicePool.Lease next = devices.Acquire();

        Assert.NotSame(first, next.Device);
        Assert.False(next.Device.IsLost);
        Assert.Equal(2, created.Count);
    }
}
