namespace Rodaje.Core.Effects;

/// <summary>
/// Funciones que el motor trae ocultas y que un complemento activa (D-037): sus efectos se aplican siempre que un proyecto
/// los tenga, pero la interfaz solo los ofrece con el complemento instalado.
/// </summary>
public static class EffectFeatures
{
    /// <summary>Video 360: reencuadre equirectangular y exportación con metadatos esféricos (complemento <c>rodaje-360</c>).</summary>
    public const string Video360 = "360";
}
