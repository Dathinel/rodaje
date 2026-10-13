using Rodaje.Render.Text;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Lo que hace el hilo de render del reproductor al cargar otro proyecto (o secuencia). Las texturas de los títulos viven
/// en la caché del rasterizador, que es del reproductor y no del proyecto: sin soltarlas, las del anterior quedaban en la
/// VRAM hasta que el tope de la caché las expulsara.
/// </summary>
/// <remarks>Separado de <c>PlayerEngine</c> para que las pruebas de fugas recorran el mismo camino sin cadena de intercambio.</remarks>
internal static class ProjectSwitch
{
    /// <summary>
    /// Abre la sesión del proyecto nuevo con <paramref name="openSession"/> (que cierra la anterior) y luego suelta de
    /// <paramref name="titles"/> lo que no se usó en los dos últimos cuadros.
    /// </summary>
    public static void Load(Action openSession, TitleRasterizer? titles)
    {
        ArgumentNullException.ThrowIfNull(openSession);
        openSession();
        titles?.Trim();
    }
}
