using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>Un recorte en el cursor: la imagen entera de la capa colocada en la secuencia, el recorte y su proporción.</summary>
public readonly record struct CropBox(string ClipId, string EffectId, LayerPlacement Layer, CropRect Crop, double? Ratio)
{
    /// <summary>Un punto de la imagen (fracciones de 0 a 1) en la secuencia.</summary>
    public Vector2 ToSequence(double u, double v) => Layer.Map((float)(u * Layer.LocalWidth), (float)(v * Layer.LocalHeight));
}

/// <summary>
/// Las asas del recorte en el visor, sin WinUI. Mientras se ajusta, el visor muestra la imagen entera (sin recorte) y aquí
/// sale con la misma colocación que el render (<see cref="LayerLayout.Place"/>): ocho asas, el cuerpo y lo que hace un arrastre.
/// </summary>
public static class CropHandles
{
    public const double GripRadius = 8;

    /// <summary>El recorte del clip en <paramref name="timeline"/>; nulo si no se ve ahí o el medio no tiene imagen.</summary>
    public static CropBox? Find(Project project, Sequence sequence, Clip clip, EffectInstance effect, Time timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effect);
        if (timeline < clip.Start || timeline >= clip.End || project.Media.Find(m => m.Id == clip.MediaId) is not { Width: > 0, Height: > 0 } media)
        {
            return null;
        }

        Time at = clip.ToEffectTime(timeline);
        if (LayerLayout.Place(sequence.Width, sequence.Height, media.Width, media.Height, MaskHandles.TransformAt(clip, at) with { Crop = null })
            is not { } layer)
        {
            return null;
        }

        double? ratio = CropGeometry.Ratio(effect.Options.GetValueOrDefault(BuiltInEffects.CropOptions.Aspect));
        return new CropBox(clip.Id, effect.Id, layer, CropGeometry.Evaluate(effect, at), ratio);
    }

    /// <summary>
    /// Las esquinas de la imagen y del recorte en la secuencia (arriba izquierda, arriba derecha, abajo derecha, abajo izquierda),
    /// y los puntos medios de los bordes del recorte (izquierda, arriba, derecha, abajo).
    /// </summary>
    public static (Vector2[] Image, Vector2[] Crop, Vector2[] Edges) Outline(in CropBox box)
    {
        CropRect c = box.Crop;
        double l = c.Left, t = c.Top, r = 1 - c.Right, b = 1 - c.Bottom;
        return (
            [box.ToSequence(0, 0), box.ToSequence(1, 0), box.ToSequence(1, 1), box.ToSequence(0, 1)],
            [box.ToSequence(l, t), box.ToSequence(r, t), box.ToSequence(r, b), box.ToSequence(l, b)],
            [box.ToSequence(l, (t + b) / 2), box.ToSequence((l + r) / 2, t), box.ToSequence(r, (t + b) / 2), box.ToSequence((l + r) / 2, b)]);
    }

    /// <summary>Qué asa queda bajo el puntero (en el panel): esquina, borde, el cuerpo del recorte o nada.</summary>
    public static CropGrip Hit(Vector2[] panelCorners, Vector2[] panelEdges, Vector2 point)
    {
        ArgumentNullException.ThrowIfNull(panelCorners);
        ArgumentNullException.ThrowIfNull(panelEdges);
        for (int i = 0; i < 4; i++)
        {
            if (Vector2.Distance(panelCorners[i], point) <= GripRadius)
            {
                return CropGrip.TopLeft + i;
            }
        }

        for (int i = 0; i < 4; i++)
        {
            if (Vector2.Distance(panelEdges[i], point) <= GripRadius)
            {
                return CropGrip.Left + i;
            }
        }

        return MaskHandles.Hit(panelCorners, point) == MaskGrip.Body ? CropGrip.Body : CropGrip.None;
    }

    /// <summary>El recorte tras llevar el puntero de <paramref name="from"/> a <paramref name="to"/> (los dos en la secuencia).</summary>
    public static CropRect Dragged(in CropBox start, CropGrip grip, Vector2 from, Vector2 to)
    {
        Vector2 a = MaskHandles.ToImage(start.Layer, from), b = MaskHandles.ToImage(start.Layer, to);
        return CropGeometry.Drag(start.Crop, grip, (b.X - a.X) / start.Layer.LocalWidth, (b.Y - a.Y) / start.Layer.LocalHeight, start.Ratio,
            start.Layer.LocalWidth, start.Layer.LocalHeight);
    }
}
