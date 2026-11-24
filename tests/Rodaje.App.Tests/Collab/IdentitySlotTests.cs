using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

public sealed class IdentitySlotTests
{
    [Fact]
    public void Una_segunda_ventana_abierta_usa_otra_clave_y_al_cerrar_no_queda_rastro()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-identidad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            (string first, FileStream? firstLock) = IdentitySlot.Take(folder);
            (string second, FileStream? secondLock) = IdentitySlot.Take(folder);
            Assert.Equal(Path.Combine(folder, "identidad.bin"), first);
            Assert.Equal(Path.Combine(folder, "identidad-2.bin"), second);

            firstLock!.Dispose();
            secondLock!.Dispose();
            Assert.Empty(Directory.GetFiles(folder));

            (string again, FileStream? againLock) = IdentitySlot.Take(folder);
            using (againLock)
            {
                Assert.Equal(first, again);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
