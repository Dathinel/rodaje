using System.Collections.Frozen;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;
using Tr = Rodaje.Core.Model.BuiltInTransitions;

namespace Rodaje.App.Services.Inspector;

/// <summary>Qué rango del deslizador usa un parámetro cuando el del modelo no sirve tal cual.</summary>
public enum UiRange
{
    /// <summary>El del registro.</summary>
    Model,

    /// <summary>De menos a más el ancho de la secuencia.</summary>
    SequenceWidth,

    /// <summary>De menos a más el alto de la secuencia.</summary>
    SequenceHeight,

    /// <summary>De 0 a la mitad del clip (los fundidos).</summary>
    HalfClip,

    /// <summary>Los de <see cref="ParameterUi.Minimum"/> y <see cref="ParameterUi.Maximum"/>; el número escrito puede salirse.</summary>
    Custom,
}

/// <summary>Cómo se muestra un parámetro del registro.</summary>
/// <param name="Label">Etiqueta en la interfaz.</param>
/// <param name="Description">Texto del historial al cambiarlo.</param>
/// <param name="Unit">Unidad que se ve («px», «%», «°», «dB», «s», «EV»), o vacía.</param>
/// <param name="DisplayFactor">Unidades de la interfaz por unidad del modelo.</param>
/// <param name="Step">Paso del deslizador, en unidades de la interfaz.</param>
/// <param name="Decimals">Decimales del número.</param>
/// <param name="Range">De dónde sale el rango del deslizador.</param>
/// <param name="Minimum">Mínimo en unidades de la interfaz si <paramref name="Range"/> es <see cref="UiRange.Custom"/>.</param>
/// <param name="Maximum">Máximo en unidades de la interfaz si <paramref name="Range"/> es <see cref="UiRange.Custom"/>.</param>
public sealed record ParameterUi(
    string Label,
    string Description,
    string Unit,
    double DisplayFactor = 1,
    double Step = 1,
    int Decimals = 0,
    UiRange Range = UiRange.Model,
    double Minimum = 0,
    double Maximum = 0);

/// <summary>
/// Nombres en español, unidades y pasos de cada efecto, transición y parámetro del registro
/// (<see cref="EffectRegistry"/>). Los límites y valores de fábrica no se repiten aquí: salen del registro.
/// </summary>
public static class EffectUi
{
    private static readonly ParameterUi Percent = new("", "", "%", 100, 1, 0);

    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>
    {
        [BuiltInEffects.Transform] = "Transformar",
        [BuiltInEffects.Color] = "Color y LUT",
        [BuiltInEffects.Vignette] = "Viñeta",
        [BuiltInEffects.Blur] = "Desenfoque",
        [BuiltInEffects.Sharpen] = "Nitidez",
        [BuiltInEffects.Blend] = "Modo de mezcla",
        [BuiltInEffects.Audio] = "Volumen y fundidos",
        [BuiltInEffects.Speed] = "Velocidad",
        [BuiltInEffects.Stabilize] = "Estabilizar",
        [Tr.Crossfade] = "Fundido cruzado",
        [Tr.Dip] = "Fundido a color",
        [Tr.Wipe] = "Barrido",
        [Tr.Push] = "Empujar",
        [Tr.ZoomDissolve] = "Fundido con zoom",
        [Tr.BlurDissolve] = "Fundido con desenfoque",
        [Tr.Preset] = "Transición de paquete",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [BuiltInEffects.Transform] = "Posición, escala, rotación y opacidad, con keyframes.",
        [BuiltInEffects.Color] = "Exposición, contraste, saturación, temperatura, luces, sombras y LUT .cube.",
        [BuiltInEffects.Vignette] = "Oscurece los bordes del cuadro.",
        [BuiltInEffects.Blur] = "Desenfoque suave de toda la imagen.",
        [BuiltInEffects.Sharpen] = "Realza los bordes.",
        [BuiltInEffects.Blend] = "Cómo se mezcla la capa con lo que tiene debajo.",
        [BuiltInEffects.Audio] = "Volumen con keyframes y fundidos de entrada y salida.",
        [BuiltInEffects.Speed] = "Cámara lenta o rápida. Todavía no se aplica.",
        [BuiltInEffects.Stabilize] = "Quita el temblor de la cámara. Todavía no se aplica.",
        [Tr.Crossfade] = "Mezcla un clip con el siguiente.",
        [Tr.Dip] = "Baja a un color (negro por defecto) y sube desde él.",
        [Tr.Wipe] = "Un borde suave barre un clip y descubre el otro.",
        [Tr.Push] = "El clip que entra empuja al que sale.",
        [Tr.ZoomDissolve] = "Fundido con un acercamiento.",
        [Tr.BlurDissolve] = "Fundido a través de un desenfoque.",
        [Tr.Preset] = "Curvas de un paquete; si falta, se ve como fundido cruzado.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<(string Type, string Name), ParameterUi> Parameters =
        new Dictionary<(string, string), ParameterUi>
        {
            [(BuiltInEffects.Transform, T.X)] = new("Posición X", "Mover en X", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.Transform, T.Y)] = new("Posición Y", "Mover en Y", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.Transform, T.Scale)] = new("Escala", "Cambiar escala", "%", 100, 1, 1, UiRange.Custom, 1, 400),
            [(BuiltInEffects.Transform, T.Rotation)] = new("Rotación", "Girar", "°", 1, 1, 1, UiRange.Custom, -360, 360),
            [(BuiltInEffects.Transform, T.Opacity)] = Percent with { Label = "Opacidad", Description = "Cambiar opacidad" },

            [(BuiltInEffects.Color, C.Exposure)] = new("Exposición", "Cambiar exposición", "EV", 1, 0.05, 2),
            [(BuiltInEffects.Color, C.Contrast)] = Percent with { Label = "Contraste", Description = "Cambiar contraste" },
            [(BuiltInEffects.Color, C.Saturation)] = Percent with { Label = "Saturación", Description = "Cambiar saturación" },
            [(BuiltInEffects.Color, C.Temperature)] = Percent with { Label = "Temperatura", Description = "Cambiar temperatura" },
            [(BuiltInEffects.Color, C.Tint)] = Percent with { Label = "Matiz", Description = "Cambiar matiz" },
            [(BuiltInEffects.Color, C.Highlights)] = Percent with { Label = "Luces", Description = "Cambiar luces" },
            [(BuiltInEffects.Color, C.Shadows)] = Percent with { Label = "Sombras", Description = "Cambiar sombras" },
            [(BuiltInEffects.Color, C.LutIntensity)] = Percent with { Label = "Intensidad de la LUT", Description = "Cambiar intensidad de la LUT" },

            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Amount)] = Percent with { Label = "Cantidad", Description = "Cambiar viñeta" },
            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Size)] = Percent with { Label = "Tamaño", Description = "Cambiar tamaño de la viñeta" },
            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Softness)] = Percent with { Label = "Suavidad", Description = "Cambiar suavidad de la viñeta" },

            [(BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius)] =
                new("Radio", "Cambiar desenfoque", "px", 1, 0.5, 1, UiRange.Custom, 0, 100),

            [(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Amount)] =
                new("Cantidad", "Cambiar nitidez", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Radius)] =
                new("Radio", "Cambiar radio de nitidez", "px", 1, 0.1, 1),

            [(BuiltInEffects.Audio, A.GainDb)] = new("Volumen", "Cambiar volumen", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Audio, A.FadeIn)] = new("Fundido de entrada", "Fundido de entrada", "s", 1, 0.1, 1, UiRange.HalfClip),
            [(BuiltInEffects.Audio, A.FadeOut)] = new("Fundido de salida", "Fundido de salida", "s", 1, 0.1, 1, UiRange.HalfClip),

            [(Tr.Dip, Tr.DipParameters.Red)] = Percent with { Label = "Rojo", Description = "Cambiar color del fundido" },
            [(Tr.Dip, Tr.DipParameters.Green)] = Percent with { Label = "Verde", Description = "Cambiar color del fundido" },
            [(Tr.Dip, Tr.DipParameters.Blue)] = Percent with { Label = "Azul", Description = "Cambiar color del fundido" },
            [(Tr.Wipe, Tr.WipeParameters.Angle)] = new("Ángulo", "Cambiar ángulo del barrido", "°", 1, 1, 0),
            [(Tr.Wipe, Tr.WipeParameters.Softness)] = Percent with { Label = "Borde suave", Description = "Cambiar borde del barrido" },
            [(Tr.ZoomDissolve, Tr.ZoomDissolveParameters.Zoom)] =
                new("Acercamiento", "Cambiar acercamiento", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(Tr.BlurDissolve, Tr.BlurDissolveParameters.Radius)] =
                new("Radio", "Cambiar desenfoque de la transición", "px", 1, 1, 0, UiRange.Custom, 0, 200),
        }.ToFrozenDictionary();

    /// <summary>Los 13 modos de mezcla en el orden del registro, con su nombre.</summary>
    public static IReadOnlyList<(string Mode, string Name)> BlendModes { get; } =
    [
        ("normal", "Normal"),
        ("add", "Sumar"),
        ("multiply", "Multiplicar"),
        ("screen", "Trama"),
        ("overlay", "Superponer"),
        ("softLight", "Luz suave"),
        ("hardLight", "Luz fuerte"),
        ("darken", "Oscurecer"),
        ("lighten", "Aclarar"),
        ("difference", "Diferencia"),
        ("exclusion", "Exclusión"),
        ("colorDodge", "Sobreexponer color"),
        ("colorBurn", "Subexponer color"),
    ];

    /// <summary>Hacia dónde sale el clip en <see cref="Tr.Push"/>, en el orden del registro.</summary>
    public static IReadOnlyList<(string Value, string Name)> PushDirections { get; } =
    [
        ("left", "Hacia la izquierda"),
        ("right", "Hacia la derecha"),
        ("up", "Hacia arriba"),
        ("down", "Hacia abajo"),
    ];

    /// <summary>Nombre para mostrar; un tipo desconocido (de un complemento que falta) se muestra tal cual.</summary>
    public static string Name(string type) => Names.GetValueOrDefault(type, type);

    public static string Description(string type) => Descriptions.GetValueOrDefault(type, "");

    public static ParameterUi? Find(string type, string parameter) => Parameters.GetValueOrDefault((type, parameter));

    public static string BlendModeName(string mode)
    {
        foreach ((string value, string name) in BlendModes)
        {
            if (value == mode)
            {
                return name;
            }
        }

        return mode;
    }

    /// <summary>Etiqueta de una opción de elección (modo de mezcla o dirección).</summary>
    public static string ChoiceName(string option, string value) => option switch
    {
        BuiltInEffects.BlendOptions.Mode => BlendModeName(value),
        Tr.PushOptions.Direction => PushDirections.FirstOrDefault(d => d.Value == value).Name ?? value,
        _ => value,
    };

    public static string OptionLabel(string option) => option switch
    {
        BuiltInEffects.BlendOptions.Mode => "Modo de mezcla",
        BuiltInEffects.ColorOptions.Lut => "LUT",
        Tr.PushOptions.Direction => "Dirección",
        Tr.PresetOptions.Preset => "Transición",
        _ => option,
    };
}
