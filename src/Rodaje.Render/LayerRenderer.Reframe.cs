using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;

namespace Rodaje.Render;

/// <summary>
/// Video 360: el reencuadre equirectangular va antes que los demás efectos del intermedio. El primer intermedio lleva el
/// equirectangular entero al tamaño del origen (hasta <see cref="MaxEquirectangularSide"/>) y este paso lo proyecta a la
/// vista plana del tamaño en pantalla, donde siguen los demás efectos.
/// </summary>
internal sealed unsafe partial class LayerRenderer
{
    /// <summary>Lado mayor del equirectangular en el intermedio: 8K de ancho ya da más detalle del que cabe en una vista 4K.</summary>
    internal const int MaxEquirectangularSide = 8192;

    private static readonly int Reframe360Code = EffectRegistry.Find(BuiltInEffects.Reframe360)!.Code;

    /// <summary>Si la capa lleva un reencuadre 360: entonces ocupa la secuencia como un video plano.</summary>
    private static bool Reframes(LayerFrame layer)
    {
        foreach (LayerEffect effect in layer.Effects)
        {
            if (effect.Code == Reframe360Code)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Width, int Height) EquirectangularSize(LayerSource source)
    {
        (int w, int h) = source.Planes is { } planes ? (planes.Width, planes.Height) : (source.Image.Width, source.Image.Height);
        float shrink = Math.Min(1f, (float)MaxEquirectangularSide / Math.Max(w, h));
        return (Math.Max(2, (int)(w * shrink)), Math.Max(1, (int)(h * shrink)));
    }

    private ImageView? Reframe(ImageView equirectangular, in SpatialStep step, int width, int height)
    {
        EffectValues values = step.Values;
        EffectDefinition definition = step.Definition;
        bool stereographic = Choice(values, definition, BuiltInEffects.Reframe360Options.Projection) == 1;
        (Equirectangular.Vector3 right, Equirectangular.Vector3 up) = Equirectangular.Basis(
            Value(values, definition, 0), Value(values, definition, 1), Value(values, definition, 2));
        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.Reframe360, (float)Equirectangular.HalfWidth(Value(values, definition, 3), stereographic),
            (float)width / height, stereographic ? 1 : 0);
        constants.Fx1 = new((float)right.X, (float)right.Y, (float)right.Z, 0);
        constants.Fx2 = new((float)up.X, (float)up.Y, (float)up.Z, 0);
        return FxPass(equirectangular, null, constants, width, height);
    }
}
