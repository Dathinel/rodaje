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

/// <summary>
/// Un cuadro del seguimiento de una máscara (tiempo del origen): su caja y, si se siguieron los vértices, la forma.
/// </summary>
public readonly record struct MaskTrackKey(Time Time, double X, double Y, double Width, double Height, double Rotation,
    IReadOnlyList<MaskVertex>? Vertices = null);

/// <summary>Qué escribe el seguimiento de una máscara.</summary>
[Flags]
public enum MaskTrackParts
{
    None = 0,
    Position = 1,
    Scale = 2,
    Rotation = 4,

    /// <summary>Los vértices, cada uno con el movimiento de su zona (keyframes de forma).</summary>
    Shape = 8,
    Similarity = Position | Scale | Rotation,
}

/// <summary>Un efecto que se tapa por zonas con una máscara (preset «Desenfocar zona» o «Pixelar zona»).</summary>
public enum ZoneEffect
{
    Blur,
    Pixelate,
}

/// <summary>Máscaras de un clip (<see cref="BuiltInEffects.Mask"/>) y las ediciones que vienen de «Quitar fondo» y «Reencuadre».</summary>
public static class MaskEdits
{
    /// <summary>Colores que van tomando las máscaras nuevas en el visor.</summary>
    public static readonly string[] Palette = ["#4FC3F7", "#FFB74D", "#81C784", "#F06292", "#BA68C8", "#FFF176", "#4DB6AC", "#E57373"];

    /// <summary>Agrega una máscara rectangular, elíptica o de trazo libre con su geometría inicial; suma a las anteriores.</summary>
    /// <param name="ctx">Contexto de la edición.</param>
    /// <param name="clipId">Clip de video.</param>
    /// <param name="shape">Forma; la de imagen se agrega con <see cref="SetImageMask"/> y las de vértices con <see cref="AddVertexMask"/>.</param>
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
        if (shape is MaskShape.Image or MaskShape.Bezier or MaskShape.Stroke)
        {
            throw new EditException(shape == MaskShape.Image
                ? "La máscara de imagen se agrega con su video gris."
                : "Las máscaras de vértices se agregan con sus vértices.");
        }

        if (shape == MaskShape.Path && (path is null || path.Count < 3))
        {
            throw new EditException("Un trazo libre necesita al menos tres puntos.");
        }

        string id = NewMask(ctx, clipId, shape, x, y, width, height);
        if (shape == MaskShape.Path)
        {
            EffectEdits.SetOption(ctx, clipId, id, O.Path, MaskGeometry.FormatPath(path!));
        }

        return id;
    }

    /// <summary>Agrega una Bézier (cerrada) o una pluma de trazo (abierta) con sus vértices relativos a la caja.</summary>
    /// <returns>Identificador del efecto nuevo.</returns>
    public static string AddVertexMask(EditContext ctx, string clipId, MaskShape shape, double x, double y, double width, double height,
        IReadOnlyList<MaskVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(vertices);
        if (shape is not (MaskShape.Bezier or MaskShape.Stroke))
        {
            throw new EditException("Solo las Bézier y la pluma de trazo llevan vértices.");
        }

        EnsureVertices(shape, vertices.Count);
        string id = NewMask(ctx, clipId, shape, x, y, width, height);
        EffectEdits.SetOption(ctx, clipId, id, O.Points, MaskPath.Format(vertices));
        return id;
    }

    private static string NewMask(EditContext ctx, string clipId, MaskShape shape, double x, double y, double width, double height)
    {
        Clip clip = ctx.Sequence.FindClip(clipId)?.Clip ?? throw new EditException($"No existe el clip «{clipId}».");
        int existing = clip.Effects.Count(e => e.Type == BuiltInEffects.Mask);
        string id = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Mask);
        EffectEdits.SetOption(ctx, clipId, id, O.Shape, MaskGeometry.ShapeName(shape));
        EffectEdits.SetOption(ctx, clipId, id, O.Mode, MaskGeometry.ModeName(MaskMode.Add));
        EffectEdits.SetOption(ctx, clipId, id, O.Name, $"Máscara {existing + 1}");
        EffectEdits.SetOption(ctx, clipId, id, O.Color, Palette[existing % Palette.Length]);
        Set(ctx, clipId, id, M.X, x);
        Set(ctx, clipId, id, M.Y, y);
        Set(ctx, clipId, id, M.Width, width);
        Set(ctx, clipId, id, M.Height, height);
        return id;
    }

    /// <summary>
    /// Cambia los vértices: si la forma está animada, pone o cambia el keyframe de <paramref name="at"/>; si no, los fijos.
    /// </summary>
    public static void SetVertices(EditContext ctx, string clipId, string maskId, IReadOnlyList<MaskVertex> vertices, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(vertices);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        MaskShape shape = MaskGeometry.ParseShape(mask.Options.GetValueOrDefault(O.Shape));
        if (shape is not (MaskShape.Bezier or MaskShape.Stroke))
        {
            throw new EditException("Solo las Bézier y la pluma de trazo llevan vértices.");
        }

        EnsureVertices(shape, vertices.Count);
        if (MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) is { } track)
        {
            EnsureInside(ctx, clipId, at);
            SetKeys(ctx, clipId, maskId, track.With(at, vertices));
            return;
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.Points, MaskPath.Format(vertices));
    }

    /// <summary>Pone (o cambia) el keyframe de forma en <paramref name="at"/> con la forma que se ve ahí: anima la forma si no lo estaba.</summary>
    public static void SetShapeKey(EditContext ctx, string clipId, string maskId, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        EnsureInside(ctx, clipId, at);
        MaskVertex[] current = MaskGeometry.Vertices(mask, at);
        if (current.Length < 2)
        {
            throw new EditException("Esta máscara no tiene vértices que animar.");
        }

        MaskShapeTrack track = MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys))?.With(at, current) ?? FromSingle(at, current);
        SetKeys(ctx, clipId, maskId, track);
    }

    private static MaskShapeTrack FromSingle(Time at, MaskVertex[] vertices) =>
        MaskShapeTrack.Parse(MaskShapeTrack.Format([(at, vertices)]))!;

    /// <summary>Quita el keyframe de forma de <paramref name="at"/>; si era el último, la forma queda fija como estaba.</summary>
    public static void RemoveShapeKey(EditContext ctx, string clipId, string maskId, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        if (MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys)) is not { } track || track.IndexOf(at) < 0)
        {
            throw new EditException("No hay un keyframe de forma en ese instante.");
        }

        if (track.Without(at) is { } rest)
        {
            SetKeys(ctx, clipId, maskId, rest);
            return;
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.Points, MaskPath.Format(track.ShapeAt(0)));
        EffectEdits.SetOption(ctx, clipId, maskId, O.ShapeKeys, "");
    }

    /// <summary>Quita todos los keyframes de forma y deja fija la forma de <paramref name="at"/>.</summary>
    public static void ClearShapeKeys(EditContext ctx, string clipId, string maskId, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        MaskVertex[] current = MaskGeometry.Vertices(mask, at);
        if (current.Length >= 2)
        {
            EffectEdits.SetOption(ctx, clipId, maskId, O.Points, MaskPath.Format(current));
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.ShapeKeys, "");
    }

    /// <summary>
    /// Pasa un rectángulo, una elipse o un trazo libre a Bézier con la misma caja y la misma forma (el rectángulo redondeado
    /// con sus arcos), para editar sus vértices.
    /// </summary>
    /// <param name="ctx">Contexto de la edición.</param>
    /// <param name="clipId">Clip de video.</param>
    /// <param name="maskId">La máscara.</param>
    /// <param name="at">Instante (del origen) cuya forma se toma.</param>
    /// <param name="imageAspect">Ancho entre alto de la imagen de la capa, para que las esquinas redondeadas sigan siendo arcos.</param>
    public static void ConvertToBezier(EditContext ctx, string clipId, string maskId, Time at, double imageAspect = 16.0 / 9)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        MaskSpec spec = MaskGeometry.Evaluate(mask, at);
        MaskVertex[] vertices = spec.Shape switch
        {
            MaskShape.Rectangle when spec.Roundness > 0 => MaskPath.RoundedRectangle(spec.Width * imageAspect, spec.Height, spec.Roundness),
            MaskShape.Rectangle => MaskPath.Rectangle(),
            MaskShape.Ellipse => MaskPath.Ellipse(),
            MaskShape.Path => [.. MaskGeometry.ParsePath(mask.Options.GetValueOrDefault(O.Path)).Select(p => new MaskVertex(p.X, p.Y))],
            _ => throw new EditException("Esta máscara ya tiene vértices o es de imagen."),
        };
        if (vertices.Length < 3)
        {
            throw new EditException("El trazo de esta máscara no se puede leer.");
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.Shape, MaskGeometry.ShapeName(MaskShape.Bezier));
        EffectEdits.SetOption(ctx, clipId, maskId, O.Points, MaskPath.Format(vertices));
        EffectEdits.SetOption(ctx, clipId, maskId, O.Path, "");
        if (mask.Parameters.ContainsKey(M.Roundness))
        {
            EffectEdits.ResetParameters(ctx, clipId, maskId, new Dictionary<string, double> { [M.Roundness] = 0 });
        }
    }

    /// <summary>
    /// La máscara limita el efecto <paramref name="targetId"/> del mismo clip en vez de la capa; vacío vuelve a la capa.
    /// </summary>
    public static void SetTarget(EditContext ctx, string clipId, string maskId, string targetId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(targetId);
        FindMask(ctx, clipId, maskId, out Clip clip);
        if (targetId.Length > 0 && clip.Effects.Find(e => e.Id == targetId) is not { } target)
        {
            throw new EditException("El efecto elegido ya no está en el clip.");
        }
        else if (targetId.Length > 0 && !CanBeMasked(clip.Effects.Find(e => e.Id == targetId)!.Type))
        {
            throw new EditException("Ese efecto no se puede limitar con una máscara.");
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.Target, targetId);
    }

    /// <summary>Si una máscara puede limitar un efecto de este tipo: los de imagen que cambian píxeles.</summary>
    public static bool CanBeMasked(string type) =>
        type != BuiltInEffects.Mask && EffectRegistry.Find(type) is { Stage: EffectStage.PerPixel or EffectStage.Spatial };

    /// <summary>Una copia de la máscara justo debajo de ella, con otro nombre.</summary>
    /// <returns>Identificador de la copia.</returns>
    public static string Duplicate(EditContext ctx, string clipId, string maskId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out Clip clip);
        EffectInstance copy = mask.Clone();
        copy.Id = ctx.Ids.NewId("effect");
        string name = mask.Options.GetValueOrDefault(O.Name) ?? "Máscara";
        copy.Options[O.Name] = name.Length > 120 ? name[..120] : name + " (copia)";
        clip.Effects.Insert(clip.Effects.IndexOf(mask) + 1, copy);
        return copy.Id;
    }

    /// <summary>
    /// «Desenfocar zona» o «Pixelar zona»: el efecto al final de la pila y una elipse suave que lo limita, en la caja dada.
    /// </summary>
    /// <returns>Los identificadores del efecto y de la máscara.</returns>
    public static (string EffectId, string MaskId) AddZone(EditContext ctx, string clipId, ZoneEffect kind, double x = 0.5, double y = 0.4,
        double width = 0.22, double height = 0.3)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        string type = kind == ZoneEffect.Blur ? BuiltInEffects.Blur : BuiltInEffects.Pixelate;
        string effectId = EffectEdits.AddEffect(ctx, clipId, type);
        if (kind == ZoneEffect.Blur)
        {
            Set(ctx, clipId, effectId, BuiltInEffects.BlurParameters.Radius, 30);
        }

        string maskId = NewMask(ctx, clipId, MaskShape.Ellipse, x, y, width, height);
        Set(ctx, clipId, maskId, M.Feather, 0.03);
        EffectEdits.SetOption(ctx, clipId, maskId, O.Target, effectId);
        EffectEdits.SetOption(ctx, clipId, maskId, O.Name, kind == ZoneEffect.Blur ? "Zona desenfocada" : "Zona pixelada");
        return (effectId, maskId);
    }

    /// <summary>
    /// Escribe el seguimiento: keyframes lineales de las partes pedidas en cada cuadro de <paramref name="keys"/>. Los
    /// keyframes que había entre el primero y el último se reemplazan; los de afuera quedan.
    /// </summary>
    public static void ApplyTrack(EditContext ctx, string clipId, string maskId, IReadOnlyList<MaskTrackKey> keys, MaskTrackParts parts)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(keys);
        EffectInstance mask = FindMask(ctx, clipId, maskId, out _);
        MaskTrackKey[] ordered = [.. keys.OrderBy(k => k.Time)];
        if (ordered.Length == 0)
        {
            return;
        }

        Time first = ordered[0].Time, last = ordered[^1].Time;
        foreach (MaskTrackKey key in ordered)
        {
            EnsureInside(ctx, clipId, key.Time);
        }

        // Los keyframes de fuera del tramo quedan; dentro, los del seguimiento (EffectEdits.ReplaceKeyframes, de una vez).
        void Write(string name, Func<MaskTrackKey, double> value)
        {
            var keys = new List<Keyframe>();
            if (mask.Parameters.TryGetValue(name, out Parameter? parameter))
            {
                // Un valor fijo hasta ahora queda igual a los lados del tramo seguido.
                keys.AddRange(parameter.Keyframes.Count == 0 ? [] : parameter.Keyframes.Where(k => k.Time < first || k.Time > last));
            }

            keys.AddRange(ordered.Select(k => new Keyframe(k.Time, value(k), Interpolation.Linear)));
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            EffectEdits.ReplaceKeyframes(ctx, clipId, maskId, name, keys);
        }

        if ((parts & MaskTrackParts.Position) != 0)
        {
            Write(M.X, k => k.X);
            Write(M.Y, k => k.Y);
        }

        if ((parts & MaskTrackParts.Scale) != 0)
        {
            Write(M.Width, k => k.Width);
            Write(M.Height, k => k.Height);
        }

        if ((parts & MaskTrackParts.Rotation) != 0)
        {
            Write(M.Rotation, k => k.Rotation);
        }

        if ((parts & MaskTrackParts.Shape) != 0 && ordered.All(k => k.Vertices is { Count: >= 2 }))
        {
            MaskShapeTrack? track = MaskShapeTrack.Parse(mask.Options.GetValueOrDefault(O.ShapeKeys));
            var kept = track is null ? [] : track.Keys().Where(k => k.Time < first || k.Time > last).ToList();
            kept.AddRange(ReduceShapeKeys(ordered));
            SetKeys(ctx, clipId, maskId, MaskShapeTrack.Parse(MaskShapeTrack.Format(kept))!);
        }
    }

    /// <summary>
    /// Quita los keyframes de forma que la interpolación entre sus vecinos ya da con menos de 0,0005 de la caja de
    /// diferencia en cada vértice: el seguimiento por vértice deja muchos cuadros casi en línea.
    /// </summary>
    internal static List<(Time Time, IReadOnlyList<MaskVertex> Vertices)> ReduceShapeKeys(IReadOnlyList<MaskTrackKey> keys)
    {
        const float tolerance = 0.0005f;
        var result = new List<(Time, IReadOnlyList<MaskVertex>)> { (keys[0].Time, keys[0].Vertices!) };
        int anchor = 0;
        for (int i = 1; i < keys.Count - 1; i++)
        {
            // Si saltar hasta i + 1 desde el ancla deja todos los intermedios cerca, el actual sobra.
            bool fits = true;
            for (int j = anchor + 1; j <= i && fits; j++)
            {
                double t = (double)(keys[j].Time - keys[anchor].Time).Ticks / (keys[i + 1].Time - keys[anchor].Time).Ticks;
                fits = Near(keys[anchor].Vertices!, keys[i + 1].Vertices!, keys[j].Vertices!, (float)t, tolerance);
            }

            if (!fits)
            {
                result.Add((keys[i].Time, keys[i].Vertices!));
                anchor = i;
            }
        }

        if (keys.Count > 1)
        {
            result.Add((keys[^1].Time, keys[^1].Vertices!));
        }

        return result;
    }

    private static bool Near(IReadOnlyList<MaskVertex> a, IReadOnlyList<MaskVertex> b, IReadOnlyList<MaskVertex> actual, float t, float tolerance)
    {
        if (a.Count != b.Count || a.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            MaskVertex mid = a[i].Lerp(b[i], t);
            if (Vector2.Distance(mid.Point, actual[i].Point) > tolerance || Vector2.Distance(mid.In, actual[i].In) > tolerance
                || Vector2.Distance(mid.Out, actual[i].Out) > tolerance)
            {
                return false;
            }
        }

        return true;
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
        if (existing is null)
        {
            EffectEdits.SetOption(ctx, clipId, id, O.Name, "Sujeto");
            EffectEdits.SetOption(ctx, clipId, id, O.Color, Palette[2]);
        }

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

    /// <summary>
    /// Da identificadores nuevos a los efectos de una copia de clip y lleva las máscaras que limitan un efecto a la copia de
    /// ese efecto.
    /// </summary>
    internal static void RenewIds(EditContext ctx, IEnumerable<EffectInstance> effects, string prefix)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        List<EffectInstance> list = [.. effects];
        foreach (EffectInstance effect in list)
        {
            string id = ctx.Ids.NewId(prefix);
            renamed[effect.Id] = id;
            effect.Id = id;
        }

        foreach (EffectInstance effect in list)
        {
            if (effect.Type == BuiltInEffects.Mask && effect.Options.GetValueOrDefault(O.Target) is { Length: > 0 } target
                && renamed.TryGetValue(target, out string? now))
            {
                effect.Options[O.Target] = now;
            }
        }
    }

    /// <summary>Pasa los tiempos de los keyframes de forma de una máscara por <paramref name="map"/> (pegar atributos).</summary>
    internal static void MapShapeKeys(EffectInstance effect, Func<Time, Time> map)
    {
        if (effect.Type != BuiltInEffects.Mask || MaskShapeTrack.Parse(effect.Options.GetValueOrDefault(O.ShapeKeys)) is not { Count: > 0 } track
            || effect.Options.GetValueOrDefault(O.ShapeKeys)!.IndexOf('@') < 0)
        {
            return;
        }

        var keys = new SortedDictionary<Time, IReadOnlyList<MaskVertex>>();
        foreach ((Time time, IReadOnlyList<MaskVertex> vertices) in track.Keys())
        {
            keys[map(time)] = vertices;
        }

        effect.Options[O.ShapeKeys] = MaskShapeTrack.Format(keys.Select(k => (k.Key, k.Value)));
    }

    private static void SetKeys(EditContext ctx, string clipId, string maskId, MaskShapeTrack track)
    {
        string text = track.ToString();
        if (text.Length > MaskShapeTrack.MaxTextLength)
        {
            throw new EditException("La forma tiene demasiados keyframes; quita algunos o sigue un tramo más corto.");
        }

        EffectEdits.SetOption(ctx, clipId, maskId, O.ShapeKeys, text);
    }

    private static EffectInstance FindMask(EditContext ctx, string clipId, string maskId, out Clip clip)
    {
        clip = ctx.Sequence.FindClip(clipId)?.Clip ?? throw new EditException($"No existe el clip «{clipId}».");
        EditContext.EnsureUnlocked(ctx.Sequence.FindClip(clipId)!.Value.Track);
        return clip.Effects.Find(e => e.Id == maskId && e.Type == BuiltInEffects.Mask)
            ?? throw new EditException("La máscara ya no está en el clip.");
    }

    private static void EnsureVertices(MaskShape shape, int count)
    {
        int minimum = shape == MaskShape.Stroke ? 2 : 3;
        if (count < minimum || count > MaskPath.MaxVertices)
        {
            throw new EditException(shape == MaskShape.Stroke
                ? "Una pluma de trazo necesita al menos dos puntos."
                : $"Una Bézier necesita de 3 a {MaskPath.MaxVertices} vértices.");
        }
    }

    private static void EnsureInside(EditContext ctx, string clipId, Time at)
    {
        Clip clip = ctx.Sequence.FindClip(clipId)!.Value.Clip;
        ClipTimeMap map = clip.TimeMap;
        (Time first, Time last) = map.Freeze
            ? (map.EffectTime(map.Start), map.EffectTime(map.Start) + map.Duration)
            : (map.SourceIn, map.SourceOut);
        if (at < first || at > last)
        {
            throw new EditException("El keyframe tiene que quedar dentro del clip.");
        }
    }

    // Sin keyframes, el instante no importa.
    private static void Set(EditContext ctx, string clipId, string id, string name, double value) =>
        EffectEdits.SetParameter(ctx, clipId, id, name, value, Time.Zero);
}
