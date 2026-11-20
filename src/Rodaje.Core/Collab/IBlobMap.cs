namespace Rodaje.Core.Collab;

/// <summary>
/// Puente entre las referencias <c>file:</c> de un proyecto (LUT, máscara de archivo), que son de cada equipo, y los blobs de la
/// sala (<c>blob:&lt;sha256&gt;</c>), que son de todos. Lo da quien conoce la carpeta del proyecto y el almacén de blobs
/// (<c>Rodaje.App</c>); Core no lee esos archivos. Sin puente, una referencia <c>file:</c> viaja vacía (sin LUT) y nunca como ruta.
/// </summary>
public interface IBlobMap
{
    /// <summary>La huella (SHA-256 en mayúsculas) del archivo de una referencia <c>file:</c>, o <see langword="null"/> si no se puede.</summary>
    string? Sha256Of(string fileReference);

    /// <summary>La referencia <c>file:</c> local del blob con esa huella, o <see langword="null"/> si este equipo aún no lo tiene.</summary>
    string? FileReferenceFor(string sha256);
}
