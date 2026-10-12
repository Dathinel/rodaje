using System.Globalization;
using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Editing;

/// <summary>Un keyframe de la transformación que propone «Reencuadre automático» (tiempo del origen del clip).</summary>
public readonly record struct TransformKey(Time Time, double X, double Y, double Scale);

/// <summary>Máscaras de un clip (<see cref="BuiltInEffects.Mask"/>) y las ediciones que vienen de «Quitar fondo» y «Reencuadre».</summary>
public static class MaskEdits
{
    /// <summary>Agrega una máscara rectangular, elíptica o de trazo libre con su geometría inicial.</summary>
    /// <param name="ctx">Contexto de la edición.</param>
    /// <param name="clipId">Clip de video.</param>
    /// <param name="shape">Forma; la de imagen se agrega con <see cref="SetImageMask"/>.</param>
    /// <param name="x">Centro horizontal (0–1 de la imagen).</param>
    /// <param name="y">Centro vertical.</param>
    /// <param name="width">Ancho (fracción del ancho de la imagen).</param>
    /// <param name="height">Alto (fracción del alto de la imagen).</param>
    /// <param name="path">Puntos del trazo relativos a la caja, para <see cref="MaskShape.Path"/>.</param>
    /// <returns>Identificador del efecto nuevo.</returns>
    public static string AddMask(EditContext ctx, string clipId, MaskShape shape, double x = 0.5, double y = 0.5, double width = 0.5,
        double height = 0.5, IReadOnlyList<Vector2>? path = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (shape == MaskShape.Image)
        {
            throw new EditException("La máscara de imagen se agrega con su video gris.");
        }

        if (shape == MaskShape.Path && (path is null || path.Count < 3))
        {
            throw new EditException("Un trazo libre necesita al menos tres puntos.");
        }

        string id = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Mask);
        EffectEdits.SetOption(ctx, clipId, id, O.Shape, MaskGeometry.ShapeName(shape));
        Set(ctx, clipId, id, M.X, x);
        Set(ctx, clipId, id, M.Y, y);
        Set(ctx, clipId, id, M.Width, width);
        Set(ctx, clipId, id, M.Height, height);
        if (shape == MaskShape.Path)
        {
            EffectEdits.SetOption(ctx, clipId, id, O.Path, MaskGeometry.FormatPath(path!));
        }

        return id;
    }

    /// <summary>
    /// Agrega (o reemplaza, si el clip ya tiene una) la máscara de imagen: el video gris <paramref name="matte"/>
    /// (<c>file:</c> más la ruta relativa al proyecto), cuyo primer cuadro es el instante <paramref name="matteStart"/> del origen.
    /// </summary>
    /// <returns>Identificador de la máscara.</returns>
    public static string SetImageMask(EditContext ctx, string clipId, string matte, Time matteStart)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!OptionDefinition.IsFileReference(matte))
        {
            throw new EditException("La máscara de imagen necesita «file:» y la ruta de su video.");
        }

        Clip clip = ctx.Sequence.FindClip(clipId)?.Clip ?? throw new EditException($"No existe el clip «{clipId}».");
        EffectInstance? existing = clip.Effects.Find(e => e.Type == BuiltInEffects.Mask && e.Options.GetValueOrDefault(O.Shape) == "image");
        string id = existing?.Id ?? EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Mask);
        EffectEdits.SetOption(ctx, clipId, id, O.Shape, MaskGeometry.ShapeName(MaskShape.Image));
        EffectEdits.SetOption(ctx, clipId, id, O.Matte, matte);
        EffectEdits.SetOption(ctx, clipId, id, O.MatteStart, matteStart.ToSeconds().ToString("R", CultureInfo.InvariantCulture));
        EffectEdits.SetEffectEnabled(ctx, clipId, id, true);
        return id;
    }

    /// <summary>
    /// Reemplaza la posición y la escala de la transformación del clip por <paramref name="keys"/> (la crea si falta). Los
    /// keyframes que caen fuera del clip se descartan; si no queda ninguno, vale el primero como valor fijo.
    /// </summary>
    public static void SetTransformKeys(EditContext ctx, string clipId, IReadOnlyList<TransformKey> keys)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(keys);
        Clip clip = ctx.Sequence.FindClip(clipId)?.Clip ?? throw new EditException($"No existe el clip «{clipId}».");
        string id = EffectEdits.EnsureEffect(ctx, clipId, BuiltInEffects.Transform)
            ?? clip.Effects.First(e => e.Type == BuiltInEffects.Transform).Id;
        EffectEdits.ResetParameters(ctx, clipId, id, new Dictionary<string, double> { [T.X] = 0, [T.Y] = 0, [T.Scale] = 1 });
        TransformKey[] inside = [.. keys.Where(k => k.Time >= clip.SourceIn && k.Time < clip.SourceOut).OrderBy(k => k.Time)];
        if (inside.Length <= 1)
        {
            if (inside.Length == 0 && keys.Count == 0)
            {
                return;
            }

            TransformKey only = inside.Length == 1 ? inside[0] : keys[0];
            Set(ctx, clipId, id, T.X, only.X);
            Set(ctx, clipId, id, T.Y, only.Y);
            Set(ctx, clipId, id, T.Scale, only.Scale);
            return;
        }

        foreach (TransformKey key in inside)
        {
            EffectEdits.SetKeyframe(ctx, clipId, id, T.X, key.Time, key.X, Interpolation.Linear);
            EffectEdits.SetKeyframe(ctx, clipId, id, T.Y, key.Time, key.Y, Interpolation.Linear);
            EffectEdits.SetKeyframe(ctx, clipId, id, T.Scale, key.Time, key.Scale, Interpolation.Linear);
        }
    }

    // Sin keyframes, el instante no importa.
    private static void Set(EditContext ctx, string clipId, string id, string name, double value) =>
        EffectEdits.SetParameter(ctx, clipId, id, name, value, Time.Zero);
}
