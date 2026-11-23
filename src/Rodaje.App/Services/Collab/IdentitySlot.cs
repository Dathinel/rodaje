namespace Rodaje.App.Services.Collab;

/// <summary>
/// Qué clave de equipo usa esta ventana de Rodaje. La primera, <c>identidad.bin</c>; si ya hay otra abierta en la misma
/// cuenta de Windows, <c>identidad-2.bin</c>, <c>-3</c>… (siempre la misma por orden, para volver a una sala sin pedir permiso).
/// Con la misma clave, una ventana que se une a la sala de la otra se buscaba a sí misma y no llegaba nunca.
/// </summary>
internal static class IdentitySlot
{
    private const int MaxWindows = 8;

    /// <returns>El archivo de la clave y el cerrojo que lo reserva mientras la ventana siga abierta (nulo si no se pudo tomar ninguno).</returns>
    public static (string File, FileStream? Lock) Take(string folder)
    {
        for (int i = 1; i <= MaxWindows; i++)
        {
            string file = Path.Combine(folder, i == 1 ? "identidad.bin" : $"identidad-{i}.bin");
            try
            {
                // Se borra solo al cerrarse el último identificador, también si el proceso muere: no deja rastro.
                return (file, new FileStream(file + ".en-uso", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
            }
            catch (IOException)
            {
                // Ocupada por otra ventana (o la carpeta no existe): se prueba la siguiente.
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        return (Path.Combine(folder, "identidad.bin"), null);
    }
}
