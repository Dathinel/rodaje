using Rodaje.Core.Model;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Text;

/// <summary>
/// Una animación de fábrica para la entrada (la salida es la misma al revés): curvas sobre las mismas interpolaciones de los
/// keyframes (<see cref="KeyframeMath"/>), con la unidad y el desfase que mejor le sientan.
/// </summary>
public sealed record TextAnimationPreset(string Id, string Name, string Description, TextUnit Unit, double Stagger, double Seconds,
    Func<double, Dictionary<string, List<StyleKey>>> Curves)
{
    /// <summary>La entrada con <paramref name="unit"/>, <paramref name="stagger"/> y <paramref name="seconds"/> (o los del preajuste).</summary>
    public StyleAnimation In(TextUnit? unit = null, double? stagger = null, double? seconds = null, UnitOrder order = UnitOrder.Forward)
    {
        double duration = seconds ?? Seconds;
        return new StyleAnimation
        {
            DurationSeconds = duration,
            Curves = Curves(duration),
            Unit = unit ?? Unit,
            Stagger = stagger ?? Stagger,
            Order = order,
        };
    }

    /// <summary>La salida: la entrada recorrida al revés.</summary>
    public StyleAnimation Out(TextUnit? unit = null, double? stagger = null, double? seconds = null, UnitOrder order = UnitOrder.Forward) =>
        TextAnimationPresets.Reverse(In(unit, stagger, seconds, order));
}

public static class TextAnimationPresets
{
    public static IReadOnlyList<TextAnimationPreset> All { get; } =
    [
        new("desvanecer", "Desvanecer", "Aparece de la transparencia", TextUnit.Letter, 0.6, 0.8,
            d => new() { [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d, 1)] }),
        new("subir", "Subir", "Sube desde abajo mientras aparece", TextUnit.Word, 0.5, 0.8,
            d => new()
            {
                [T.Y] = [new(0, 60, Interpolation.EaseOut), new(d, 0)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d * 0.6, 1)],
            }),
        new("bajar", "Bajar", "Cae desde arriba mientras aparece", TextUnit.Word, 0.5, 0.8,
            d => new()
            {
                [T.Y] = [new(0, -60, Interpolation.EaseOut), new(d, 0)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d * 0.6, 1)],
            }),
        new("rebote", "Rebote", "Cae y rebota hasta quedarse", TextUnit.Letter, 0.6, 1.2,
            d => new()
            {
                [T.Y] = [new(0, -120, Interpolation.Bounce), new(d, 0)],
                [T.Opacity] = [new(0, 0, Interpolation.Linear), new(d * 0.15, 1)],
            }),
        new("escala", "Escala", "Crece desde chico", TextUnit.Letter, 0.5, 0.7,
            d => new()
            {
                [T.Scale] = [new(0, 0.2, Interpolation.EaseOut), new(d, 1)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d * 0.5, 1)],
            }),
        new("elastico", "Elástico", "Crece pasándose y vuelve como un resorte", TextUnit.Letter, 0.5, 1,
            d => new()
            {
                [T.Scale] = [new(0, 0, Interpolation.Elastic), new(d, 1)],
                [T.Opacity] = [new(0, 0, Interpolation.Linear), new(d * 0.2, 1)],
            }),
        new("desenfoque", "Desenfoque", "Se enfoca mientras aparece", TextUnit.Word, 0.4, 0.9,
            d => new()
            {
                [StyleAnimation.Blur] = [new(0, 24, Interpolation.EaseOut), new(d, 0)],
                [T.Scale] = [new(0, 1.15, Interpolation.EaseOut), new(d, 1)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d * 0.7, 1)],
            }),
        new("girar", "Girar", "Gira hasta quedar derecho", TextUnit.Letter, 0.5, 0.8,
            d => new()
            {
                [T.Rotation] = [new(0, -90, Interpolation.EaseOut), new(d, 0)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d * 0.5, 1)],
            }),
        new("maquina", "Máquina de escribir", "Las letras aparecen una a una", TextUnit.Letter, 1, 1.5,
            d => new() { [T.Opacity] = [new(0, 0, Interpolation.Hold), new(d, 1)] }),
        new("lineas", "Líneas", "Cada línea sube y aparece", TextUnit.Line, 0.5, 0.9,
            d => new()
            {
                [T.Y] = [new(0, 40, Interpolation.EaseOut), new(d, 0)],
                [T.Opacity] = [new(0, 0, Interpolation.EaseOut), new(d, 1)],
            }),
    ];

    public static TextAnimationPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>El preajuste cuyas curvas coinciden con <paramref name="animation"/> como entrada o salida; nulo si es propia.</summary>
    public static TextAnimationPreset? Match(StyleAnimation? animation, bool entry)
    {
        if (animation is null)
        {
            return null;
        }

        foreach (TextAnimationPreset preset in All)
        {
            StyleAnimation candidate = entry
                ? preset.In(animation.Unit, animation.Stagger, animation.DurationSeconds, animation.Order)
                : preset.Out(animation.Unit, animation.Stagger, animation.DurationSeconds, animation.Order);
            if (candidate.ContentEquals(animation))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// La misma animación recorrida al revés (para la salida): cada curva vale en <c>t</c> lo que valía en <c>d − t</c>.
    /// Entrada y salida suave se intercambian; rebote y elástico, que no tienen inverso exacto, salen como entrada suave.
    /// </summary>
    public static StyleAnimation Reverse(StyleAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        double d = animation.DurationSeconds;
        var curves = new Dictionary<string, List<StyleKey>>(StringComparer.Ordinal);
        foreach ((string name, List<StyleKey> keys) in animation.Curves)
        {
            var reversed = new List<StyleKey>(keys.Count);
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                Interpolation interp = i > 0 ? Mirror(keys[i - 1].Interp) : Interpolation.Linear;
                reversed.Add(new StyleKey(Math.Max(0, d - keys[i].T), keys[i].Value, interp));
            }

            if (reversed.Count > 0 && reversed[0].T > 0 && keys[^1].T < d)
            {
                // Lo que la entrada sostenía al final, la salida lo sostiene al principio.
                reversed.Insert(0, new StyleKey(0, keys[^1].Value, Interpolation.Hold));
            }

            curves[name] = reversed;
        }

        return new StyleAnimation
        {
            DurationSeconds = d,
            Curves = curves,
            Unit = animation.Unit,
            Stagger = animation.Stagger,
            Order = animation.Order,
        };
    }

    private static Interpolation Mirror(Interpolation interpolation) => interpolation switch
    {
        Interpolation.EaseIn => Interpolation.EaseOut,
        Interpolation.EaseOut or Interpolation.Bounce or Interpolation.Elastic => Interpolation.EaseIn,
        _ => interpolation,
    };
}
