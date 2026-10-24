using System.Collections.Frozen;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;
using Ck = Rodaje.Core.Model.BuiltInEffects.ChromaKeyParameters;
using Cr = Rodaje.Core.Model.BuiltInEffects.CropParameters;
using Mk = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using Wh = Rodaje.Core.Model.BuiltInEffects.ColorWheelsParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;
using Tp = Rodaje.Core.Model.BuiltInTransitions.CommonParameters;
using Tr = Rodaje.Core.Model.BuiltInTransitions;
using Gr = Rodaje.Core.Model.BuiltInEffects.GrainParameters;
using Gl = Rodaje.Core.Model.BuiltInEffects.GlowParameters;
using Ab = Rodaje.Core.Model.BuiltInEffects.ChromaticAberrationParameters;
using Dn = Rodaje.Core.Model.BuiltInEffects.DenoiseParameters;
using Mi = Rodaje.Core.Model.BuiltInEffects.MirrorParameters;
using Ld = Rodaje.Core.Model.BuiltInEffects.LensDistortionParameters;
using R360 = Rodaje.Core.Model.BuiltInEffects.Reframe360Parameters;
using Hq = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;
using Vg = Rodaje.Core.Model.BuiltInEffects.VignetteParameters;
using Bl = Rodaje.Core.Model.BuiltInEffects.BlurParameters;

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
        [BuiltInEffects.Equalizer] = "Ecualizador",
        [BuiltInEffects.Compressor] = "Compresor",
        [BuiltInEffects.GraphicEqualizer] = "Ecualizador gráfico",
        [BuiltInEffects.Gate] = "Puerta de ruido",
        [BuiltInEffects.DeEsser] = "De-esser",
        [BuiltInEffects.Saturation] = "Saturación",
        [BuiltInEffects.Delay] = "Delay",
        [BuiltInEffects.Reverb] = "Reverb",
        [BuiltInEffects.PitchShift] = "Tono",
        [BuiltInEffects.Limiter] = "Limitador",
        [BuiltInEffects.Speed] = "Velocidad",
        [BuiltInEffects.Stabilize] = "Estabilizar",
        [BuiltInEffects.Mask] = "Máscara",
        [BuiltInEffects.Crop] = "Recortar",
        [BuiltInEffects.ChromaKey] = "Croma (fondo verde)",
        [BuiltInEffects.ColorWheels] = "Ruedas de color",
        [BuiltInEffects.Curves] = "Curvas",
        [BuiltInEffects.Pixelate] = "Pixelar",
        [BuiltInEffects.CornerPin] = "Anclaje de esquinas",
        [BuiltInEffects.Track] = "Seguimiento",
        [BuiltInEffects.Grain] = "Grano de película",
        [BuiltInEffects.Glow] = "Resplandor",
        [BuiltInEffects.ChromaticAberration] = "Aberración cromática",
        [BuiltInEffects.Denoise] = "Reducción de ruido",
        [BuiltInEffects.Mirror] = "Espejo y pantalla dividida",
        [BuiltInEffects.LensDistortion] = "Distorsión de lente",
        [BuiltInEffects.Reframe360] = "Reencuadre 360",
        [BuiltInEffects.HslQualifier] = "Corrección secundaria (HSL)",
        [Tr.Crossfade] = "Fundido cruzado",
        [Tr.Dip] = "Fundido a color",
        [Tr.Wipe] = "Barrido",
        [Tr.Push] = "Empujar",
        [Tr.ZoomDissolve] = "Fundido con zoom",
        [Tr.BlurDissolve] = "Fundido con desenfoque",
        [Tr.Slide] = "Deslizar",
        [Tr.ZoomBlur] = "Zoom con desenfoque",
        [Tr.Spin] = "Giro",
        [Tr.LightWipe] = "Barrido con luz",
        [Tr.Glitch] = "Fallo digital",
        [Tr.FilmBurn] = "Quemado de película",
        [Tr.Flash] = "Destello",
        [Tr.Luma] = "Disolución por luma",
        [Tr.Bounce] = "Empujar con rebote",
        [Tr.AudioPower] = "Fundido de potencia constante",
        [Tr.AudioGain] = "Fundido de ganancia constante",
        [Tr.AudioExponential] = "Fundido exponencial",
        [Tr.Preset] = "Transición de paquete",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [BuiltInEffects.Transform] = "Posición, escala, rotación, opacidad, ancla, inclinación, volteo y desenfoque de movimiento, con keyframes.",
        [BuiltInEffects.Color] = "Exposición, contraste, saturación, temperatura, luces, sombras y LUT .cube.",
        [BuiltInEffects.Vignette] = "Oscurece los bordes del cuadro.",
        [BuiltInEffects.Blur] = "Gaussiano, de lente (con forma de disco), direccional o radial.",
        [BuiltInEffects.Sharpen] = "Máscara de enfoque con umbral: realza los bordes sin el ruido.",
        [BuiltInEffects.Blend] = "Cómo se mezcla la capa con lo que tiene debajo.",
        [BuiltInEffects.Audio] = "Volumen con keyframes y fundidos de entrada y salida.",
        [BuiltInEffects.Equalizer] = "Corte de graves, graves, medios, presencia y agudos.",
        [BuiltInEffects.Compressor] = "Empareja el volumen: baja lo que pasa del umbral y compensa.",
        [BuiltInEffects.GraphicEqualizer] = "Diez bandas de una octava, de 31 Hz a 16 kHz, ±12 dB.",
        [BuiltInEffects.Gate] = "Calla el ruido entre frases: baja lo que queda por debajo del umbral (puerta o expansor suave).",
        [BuiltInEffects.DeEsser] = "Suaviza las eses y los silbidos de la voz sin apagarla.",
        [BuiltInEffects.Saturation] = "Calor de cinta o de válvula: armónicos suaves sin aspereza.",
        [BuiltInEffects.Delay] = "Ecos con realimentación filtrada, libres o al tempo, y ping-pong.",
        [BuiltInEffects.Reverb] = "Sala, estudio o hall: la cola de un espacio (algoritmo FDN propio).",
        [BuiltInEffects.PitchShift] = "Sube o baja el tono sin cambiar la duración.",
        [BuiltInEffects.Limiter] = "No deja pasar del techo, ni entre muestras (pico verdadero).",
        [BuiltInEffects.Speed] = "Reservado y oculto: la velocidad se cambia en la pestaña Velocidad.",
        [BuiltInEffects.Stabilize] = "Quita el temblor de la cámara. Se analiza desde el menú del clip (Estabilizar…).",
        [BuiltInEffects.Mask] = "Deja ver solo una parte de la capa o de un efecto: rectángulo, elipse, Bézier, pluma, trazo libre o el sujeto de «Quitar fondo».",
        [BuiltInEffects.Pixelate] = "Bloques del color promedio de su zona; con una máscara tapa caras o matrículas.",
        [BuiltInEffects.CornerPin] = "Lleva cada esquina de la imagen a otro lugar con perspectiva: reemplazar una pantalla o un cartel.",
        [BuiltInEffects.Track] = "Puntos seguidos cuadro a cuadro en el clip, para aplicarlos a otro clip, una máscara o una estabilización.",
        [BuiltInEffects.Crop] = "Quita bordes de la imagen, libre o con proporción fija (16:9, 9:16, 1:1, 4:5, 4:3); se ajusta con asas en el visor.",
        [BuiltInEffects.ChromaKey] = "Vuelve transparente un color de fondo (pantalla verde o azul) con cuentagotas, suavidad y quitar derrame.",
        [BuiltInEffects.ColorWheels] = "Sombras, medios y luces: un tinte y un nivel para cada zona.",
        [BuiltInEffects.Curves] = "Curva general y por canal, y tono contra tono, tono contra saturación y luma contra saturación.",
        [BuiltInEffects.Grain] = "Grano que cambia en cada cuadro, sobre todo en los medios tonos.",
        [BuiltInEffects.Glow] = "Las luces se abren en un halo suave, sumado en luz lineal.",
        [BuiltInEffects.ChromaticAberration] = "Separa el rojo y el azul hacia los bordes, como una lente.",
        [BuiltInEffects.Denoise] = "Suaviza el ruido respetando los bordes y, al reproducir y exportar, con el cuadro anterior.",
        [BuiltInEffects.Mirror] = "Refleja una mitad o repite la imagen en mosaico.",
        [BuiltInEffects.LensDistortion] = "Curva la imagen hacia afuera (barril) o hacia adentro (cojín).",
        [BuiltInEffects.Reframe360] = "Elige hacia dónde mira la cámara dentro de un video 360 y lo convierte en un video plano.",
        [BuiltInEffects.HslQualifier] = "Elige un color por tono, saturación y luma y cambia solo ese: tono, saturación y exposición.",
        [Tr.Crossfade] = "Mezcla un clip con el siguiente.",
        [Tr.Dip] = "Baja a un color (negro por defecto) y sube desde él.",
        [Tr.Wipe] = "Un borde suave barre un clip y descubre el otro.",
        [Tr.Push] = "El clip que entra empuja al que sale.",
        [Tr.ZoomDissolve] = "Fundido con un acercamiento.",
        [Tr.BlurDissolve] = "Fundido a través de un desenfoque.",
        [Tr.Slide] = "El clip que entra se desliza sobre el que sale, con desenfoque de movimiento.",
        [Tr.ZoomBlur] = "Acercamiento rápido con desenfoque radial hacia un punto.",
        [Tr.Spin] = "Gira la imagen sobre el centro y la cambia en el medio de la rotación.",
        [Tr.LightWipe] = "Un barrido con una franja de luz en el borde.",
        [Tr.Glitch] = "Franjas corridas y canales separados, como una señal que falla.",
        [Tr.FilmBurn] = "Manchas de luz cálida que queman la película y dejan ver el clip que entra.",
        [Tr.Flash] = "Un destello de luz tapa el corte.",
        [Tr.Luma] = "Un degradado o una imagen gris decide qué parte cambia primero.",
        [Tr.Bounce] = "El clip que entra empuja al que sale, se pasa y vuelve.",
        [Tr.AudioPower] = "Cruce de audio sin bajón de volumen: la potencia se mantiene (seno y coseno). El más usado.",
        [Tr.AudioGain] = "Cruce lineal: las ganancias suman 1. Puede sonar con un pequeño bajón en el medio.",
        [Tr.AudioExponential] = "Cruce lineal en decibelios: el que sale baja rápido y el que entra sube tarde.",
        [Tr.Preset] = "Curvas de un paquete; si falta, se ve como fundido cruzado.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<(string Type, string Name), ParameterUi> Parameters =
        new Dictionary<(string, string), ParameterUi>
        {
            [(BuiltInEffects.Transform, T.X)] = new("Posición X", "Mover en X", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.Transform, T.Y)] = new("Posición Y", "Mover en Y", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.Transform, T.Scale)] = new("Escala", "Cambiar escala", "%", 100, 1, 1, UiRange.Custom, 1, 400),
            [(BuiltInEffects.Transform, T.Rotation)] = new("Rotación", "Girar", "°", 1, 1, 1, UiRange.Custom, -360, 360),
            [(BuiltInEffects.Transform, T.HdrReferenceNits)] = new("Brillo de referencia HDR", "Cambiar brillo de referencia HDR", "nits", Step: 10),
            [(BuiltInEffects.Transform, T.Opacity)] = Percent with { Label = "Opacidad", Description = "Cambiar opacidad" },
            [(BuiltInEffects.Transform, T.AnchorX)] = new("Ancla X", "Mover el punto de anclaje", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.Transform, T.AnchorY)] = new("Ancla Y", "Mover el punto de anclaje", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.Transform, T.ScaleX)] = new("Ancho", "Estirar a lo ancho", "%", 100, 1, 1, UiRange.Custom, 1, 400),
            [(BuiltInEffects.Transform, T.ScaleY)] = new("Alto", "Estirar a lo alto", "%", 100, 1, 1, UiRange.Custom, 1, 400),
            [(BuiltInEffects.Transform, T.SkewX)] = new("Inclinación X", "Inclinar", "°", 1, 1, 1),
            [(BuiltInEffects.Transform, T.SkewY)] = new("Inclinación Y", "Inclinar", "°", 1, 1, 1),
            [(BuiltInEffects.Transform, T.ShutterAngle)] = new("Obturador", "Cambiar el desenfoque de movimiento", "°", 1, 15, 0),
            [(BuiltInEffects.Transform, T.ShutterPhase)] = new("Fase", "Cambiar la fase del obturador", "°", 1, 15, 0),
            [(BuiltInEffects.Transform, T.MotionSamples)] = new("Muestras", "Cambiar las muestras del desenfoque", "", 1, 1, 0),

            [(BuiltInEffects.Color, C.Exposure)] = new("Exposición", "Cambiar exposición", "EV", 1, 0.05, 2),
            [(BuiltInEffects.Color, C.Contrast)] = Percent with { Label = "Contraste", Description = "Cambiar contraste" },
            [(BuiltInEffects.Color, C.Saturation)] = Percent with { Label = "Saturación", Description = "Cambiar saturación" },
            [(BuiltInEffects.Color, C.Temperature)] = Percent with { Label = "Temperatura", Description = "Cambiar temperatura" },
            [(BuiltInEffects.Color, C.Tint)] = Percent with { Label = "Matiz", Description = "Cambiar matiz" },
            [(BuiltInEffects.Color, C.Highlights)] = Percent with { Label = "Luces", Description = "Cambiar luces" },
            [(BuiltInEffects.Color, C.Shadows)] = Percent with { Label = "Sombras", Description = "Cambiar sombras" },
            [(BuiltInEffects.Color, C.LutIntensity)] = Percent with { Label = "Intensidad de la LUT", Description = "Cambiar intensidad de la LUT" },
            [(BuiltInEffects.Color, C.SkinProtection)] = Percent with { Label = "Proteger la piel", Description = "Cambiar la protección de la piel" },

            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Amount)] = Percent with { Label = "Cantidad", Description = "Cambiar viñeta" },
            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Size)] = Percent with { Label = "Tamaño", Description = "Cambiar tamaño de la viñeta" },
            [(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Softness)] = Percent with { Label = "Suavidad", Description = "Cambiar suavidad de la viñeta" },
            [(BuiltInEffects.Vignette, Vg.Roundness)] = Percent with { Label = "Redondez", Description = "Cambiar la forma de la viñeta" },
            [(BuiltInEffects.Vignette, Vg.CenterX)] = new("Centro X", "Mover la viñeta", "%", 100, 0.5, 1, UiRange.Custom, -50, 150),
            [(BuiltInEffects.Vignette, Vg.CenterY)] = new("Centro Y", "Mover la viñeta", "%", 100, 0.5, 1, UiRange.Custom, -50, 150),

            [(BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius)] =
                new("Radio", "Cambiar desenfoque", "px", 1, 0.5, 1, UiRange.Custom, 0, 100),
            [(BuiltInEffects.Blur, Bl.Angle)] = new("Ángulo", "Cambiar el ángulo del desenfoque", "°", 1, 1, 0, UiRange.Custom, -180, 180),
            [(BuiltInEffects.Blur, Bl.CenterX)] = new("Centro X", "Mover el centro del desenfoque", "%", 100, 0.5, 1, UiRange.Custom, 0, 100),
            [(BuiltInEffects.Blur, Bl.CenterY)] = new("Centro Y", "Mover el centro del desenfoque", "%", 100, 0.5, 1, UiRange.Custom, 0, 100),

            [(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Amount)] =
                new("Cantidad", "Cambiar nitidez", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Radius)] =
                new("Radio", "Cambiar radio de nitidez", "px", 1, 0.1, 1),
            [(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Threshold)] =
                new("Umbral", "Cambiar el umbral de nitidez", "%", 100, 0.5, 1, UiRange.Custom, 0, 25),

            [(BuiltInEffects.Grain, Gr.Amount)] = Percent with { Label = "Cantidad", Description = "Cambiar el grano" },
            [(BuiltInEffects.Grain, Gr.Size)] = new("Tamaño", "Cambiar el tamaño del grano", "px", 1, 0.1, 1),
            [(BuiltInEffects.Grain, Gr.Color)] = Percent with { Label = "Color", Description = "Cambiar el color del grano" },
            [(BuiltInEffects.Grain, Gr.Response)] = Percent with { Label = "En los medios", Description = "Cambiar dónde se ve el grano" },

            [(BuiltInEffects.Glow, Gl.Threshold)] = Percent with { Label = "Umbral", Description = "Cambiar el umbral del resplandor" },
            [(BuiltInEffects.Glow, Gl.Radius)] = new("Radio", "Cambiar el radio del resplandor", "px", 1, 1, 0, UiRange.Custom, 1, 200),
            [(BuiltInEffects.Glow, Gl.Intensity)] = new("Intensidad", "Cambiar la intensidad del resplandor", "%", 100, 1, 0, UiRange.Custom, 0, 400),

            [(BuiltInEffects.ChromaticAberration, Ab.Amount)] = new("Separación", "Cambiar la aberración", "px", 1, 0.5, 1, UiRange.Custom, 0, 40),
            [(BuiltInEffects.ChromaticAberration, Ab.Angle)] = new("Ángulo", "Cambiar el ángulo de la aberración", "°", 1, 1, 0, UiRange.Custom, -180, 180),

            [(BuiltInEffects.Denoise, Dn.Strength)] = Percent with { Label = "Fuerza", Description = "Cambiar la reducción de ruido" },
            [(BuiltInEffects.Denoise, Dn.Temporal)] = Percent with { Label = "Entre cuadros", Description = "Cambiar la reducción entre cuadros" },
            [(BuiltInEffects.Denoise, Dn.Detail)] = Percent with { Label = "Detalle", Description = "Cambiar el detalle que se conserva" },

            [(BuiltInEffects.Mirror, Mi.Axis)] = Percent with { Label = "Eje", Description = "Mover el eje del espejo", Step = 0.5, Decimals = 1 },
            [(BuiltInEffects.Mirror, Mi.Tiles)] = new("Copias", "Cambiar las copias del mosaico", "", 1, 1, 0),

            [(BuiltInEffects.LensDistortion, Ld.Amount)] = Percent with { Label = "Cantidad", Description = "Cambiar la distorsión" },
            [(BuiltInEffects.Reframe360, R360.Yaw)] = new("Mirar a los lados", "Girar la cámara 360 a la izquierda o a la derecha", "°", 1, 1, 0),
            [(BuiltInEffects.Reframe360, R360.Pitch)] = new("Mirar arriba o abajo", "Inclinar la cámara 360", "°", 1, 1, 0),
            [(BuiltInEffects.Reframe360, R360.Roll)] = new("Girar el horizonte", "Girar la imagen de la cámara 360", "°", 1, 1, 0),
            [(BuiltInEffects.Reframe360, R360.Fov)] = new("Campo de visión", "Abrir o cerrar el campo de visión", "°", 1, 1, 0),
            [(BuiltInEffects.LensDistortion, Ld.Zoom)] = new("Zoom", "Cambiar el acercamiento", "%", 100, 1, 0, UiRange.Custom, 50, 200),

            [(BuiltInEffects.HslQualifier, Hq.HueCenter)] = new("Tono", "Cambiar el tono elegido", "°", 1, 1, 0),
            [(BuiltInEffects.HslQualifier, Hq.HueWidth)] = new("Ancho del tono", "Cambiar el ancho del tono", "°", 1, 1, 0, UiRange.Custom, 0, 90),
            [(BuiltInEffects.HslQualifier, Hq.HueSoftness)] = new("Suavidad del tono", "Cambiar la suavidad del tono", "°", 1, 1, 0, UiRange.Custom, 0, 60),
            [(BuiltInEffects.HslQualifier, Hq.SaturationLow)] = Percent with { Label = "Saturación mín.", Description = "Cambiar la saturación mínima" },
            [(BuiltInEffects.HslQualifier, Hq.SaturationHigh)] = Percent with { Label = "Saturación máx.", Description = "Cambiar la saturación máxima" },
            [(BuiltInEffects.HslQualifier, Hq.LumaLow)] = Percent with { Label = "Luma mín.", Description = "Cambiar la luma mínima" },
            [(BuiltInEffects.HslQualifier, Hq.LumaHigh)] = Percent with { Label = "Luma máx.", Description = "Cambiar la luma máxima" },
            [(BuiltInEffects.HslQualifier, Hq.Softness)] = Percent with { Label = "Suavidad", Description = "Cambiar la suavidad de la selección" },
            [(BuiltInEffects.HslQualifier, Hq.HueShift)] = new("Girar tono", "Girar el tono de la selección", "°", 1, 1, 0),
            [(BuiltInEffects.HslQualifier, Hq.Saturation)] = Percent with { Label = "Saturación", Description = "Cambiar la saturación de la selección" },
            [(BuiltInEffects.HslQualifier, Hq.Exposure)] = new("Exposición", "Cambiar la exposición de la selección", "EV", 1, 0.05, 2),

            [(BuiltInEffects.Audio, A.GainDb)] = new("Volumen", "Cambiar volumen", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Audio, A.FadeIn)] = new("Fundido de entrada", "Fundido de entrada", "s", 1, 0.1, 1, UiRange.HalfClip),
            [(BuiltInEffects.Audio, A.FadeOut)] = new("Fundido de salida", "Fundido de salida", "s", 1, 0.1, 1, UiRange.HalfClip),
            [(BuiltInEffects.Audio, A.Pan)] = new("Paneo", "Cambiar paneo", "", 1, 1, 0),
            [(BuiltInEffects.Audio, A.Balance)] = new("Balance", "Cambiar balance", "", 1, 1, 0),
            [(BuiltInEffects.GraphicEqualizer, "band31")] = new("31 Hz", "Cambiar 31 Hz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band63")] = new("63 Hz", "Cambiar 63 Hz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band125")] = new("125 Hz", "Cambiar 125 Hz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band250")] = new("250 Hz", "Cambiar 250 Hz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band500")] = new("500 Hz", "Cambiar 500 Hz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band1k")] = new("1 kHz", "Cambiar 1 kHz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band2k")] = new("2 kHz", "Cambiar 2 kHz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band4k")] = new("4 kHz", "Cambiar 4 kHz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band8k")] = new("8 kHz", "Cambiar 8 kHz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, "band16k")] = new("16 kHz", "Cambiar 16 kHz", "dB", 1, 0.5, 1),
            [(BuiltInEffects.GraphicEqualizer, BuiltInEffects.GraphicEqualizerParameters.Output)] = new("Salida", "Cambiar salida del gráfico", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Threshold)] = new("Umbral", "Cambiar umbral de la puerta", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Range)] = new("Rango", "Cambiar rango de la puerta", "dB", 1, 1, 0),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Ratio)] = new("Relación (1:N)", "Cambiar relación de la puerta", "", 1, 0.5, 1),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Attack)] = new("Ataque", "Cambiar ataque de la puerta", "ms", 1, 0.1, 1),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Hold)] = new("Retención", "Cambiar retención de la puerta", "ms", 1, 5, 0),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Release)] = new("Relajación", "Cambiar relajación de la puerta", "ms", 1, 5, 0),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.Hysteresis)] = new("Histéresis", "Cambiar histéresis", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Gate, BuiltInEffects.GateParameters.SidechainHighPass)] = new("Pasa altos del detector", "Cambiar pasa altos de la puerta", "Hz", 1, 5, 0),
            [(BuiltInEffects.DeEsser, BuiltInEffects.DeEsserParameters.Frequency)] = new("Frecuencia", "Cambiar frecuencia del de-esser", "Hz", 1, 100, 0),
            [(BuiltInEffects.DeEsser, BuiltInEffects.DeEsserParameters.Threshold)] = new("Umbral", "Cambiar umbral del de-esser", "dB", 1, 0.5, 1),
            [(BuiltInEffects.DeEsser, BuiltInEffects.DeEsserParameters.Range)] = new("Reducción máxima", "Cambiar reducción del de-esser", "dB", 1, 0.5, 1),
            [(BuiltInEffects.DeEsser, BuiltInEffects.DeEsserParameters.SplitBand)] = new("Solo la banda alta", "Cambiar banda del de-esser", "", 1, 1, 0),
            [(BuiltInEffects.Saturation, BuiltInEffects.SaturationParameters.Drive)] = new("Empuje", "Cambiar empuje", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Saturation, BuiltInEffects.SaturationParameters.Character)] = new("Carácter (cinta a válvula)", "Cambiar carácter", "%", 1, 1, 0),
            [(BuiltInEffects.Saturation, BuiltInEffects.SaturationParameters.Tone)] = new("Tono", "Cambiar tono de la saturación", "Hz", 1, 100, 0),
            [(BuiltInEffects.Saturation, BuiltInEffects.SaturationParameters.Output)] = new("Salida", "Cambiar salida de la saturación", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Saturation, BuiltInEffects.SaturationParameters.Mix)] = new("Mezcla", "Cambiar mezcla de la saturación", "%", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Time)] = new("Tiempo", "Cambiar tiempo del delay", "ms", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Sync)] = new("Al tempo", "Cambiar sincronía del delay", "", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Tempo)] = new("Tempo", "Cambiar tempo del delay", "BPM", 1, 0.5, 1),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Division)] = new("Figura", "Cambiar figura del delay", "", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Feedback)] = new("Realimentación", "Cambiar realimentación", "%", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.PingPong)] = new("Ping-pong", "Cambiar ping-pong", "", 1, 1, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.LowCut)] = new("Corte de graves", "Cambiar graves del delay", "Hz", 1, 5, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.HighCut)] = new("Corte de agudos", "Cambiar agudos del delay", "Hz", 1, 100, 0),
            [(BuiltInEffects.Delay, BuiltInEffects.DelayParameters.Mix)] = new("Mezcla", "Cambiar mezcla del delay", "%", 1, 1, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.PreDelay)] = new("Predelay", "Cambiar predelay", "ms", 1, 1, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.Size)] = new("Tamaño", "Cambiar tamaño de la sala", "%", 1, 1, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.Decay)] = new("Duración (RT60)", "Cambiar duración de la reverb", "s", 1, 0.1, 1),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.Damping)] = new("Brillo", "Cambiar brillo de la reverb", "Hz", 1, 100, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.Width)] = new("Ancho", "Cambiar ancho de la reverb", "%", 1, 1, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.LowCut)] = new("Corte de graves", "Cambiar graves de la reverb", "Hz", 1, 5, 0),
            [(BuiltInEffects.Reverb, BuiltInEffects.ReverbParameters.Mix)] = new("Mezcla", "Cambiar mezcla de la reverb", "%", 1, 1, 0),
            [(BuiltInEffects.PitchShift, BuiltInEffects.PitchShiftParameters.Semitones)] = new("Semitonos", "Cambiar tono", "st", 1, 1, 0),
            [(BuiltInEffects.PitchShift, BuiltInEffects.PitchShiftParameters.Cents)] = new("Cents", "Afinar el tono", "ct", 1, 1, 0),
            [(BuiltInEffects.PitchShift, BuiltInEffects.PitchShiftParameters.Mix)] = new("Mezcla", "Cambiar mezcla del tono", "%", 1, 1, 0),
            [(BuiltInEffects.Limiter, BuiltInEffects.LimiterParameters.Ceiling)] = new("Techo", "Cambiar techo del limitador", "dBTP", 1, 0.1, 1),
            [(BuiltInEffects.Limiter, BuiltInEffects.LimiterParameters.InputGain)] = new("Ganancia de entrada", "Cambiar ganancia del limitador", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Limiter, BuiltInEffects.LimiterParameters.Release)] = new("Recuperación", "Cambiar recuperación del limitador", "ms", 1, 5, 0),

            [(BuiltInEffects.Equalizer, Q.LowCut)] = new("Corte de graves", "Cambiar corte de graves", "Hz", 1, 5, 0),
            [(BuiltInEffects.Equalizer, Q.LowGain)] = new("Graves", "Cambiar graves", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Equalizer, Q.LowFrequency)] = new("Frecuencia", "Cambiar frecuencia de graves", "Hz", 1, 5, 0),
            [(BuiltInEffects.Equalizer, Q.MidGain)] = new("Medios", "Cambiar medios", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Equalizer, Q.MidFrequency)] = new("Frecuencia", "Cambiar frecuencia de medios", "Hz", 1, 10, 0),
            [(BuiltInEffects.Equalizer, Q.MidQ)] = new("Ancho (Q)", "Cambiar ancho de medios", "", 1, 0.1, 1),
            [(BuiltInEffects.Equalizer, Q.PresenceGain)] = new("Presencia", "Cambiar presencia", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Equalizer, Q.PresenceFrequency)] = new("Frecuencia", "Cambiar frecuencia de presencia", "Hz", 1, 50, 0),
            [(BuiltInEffects.Equalizer, Q.PresenceQ)] = new("Ancho (Q)", "Cambiar ancho de presencia", "", 1, 0.1, 1),
            [(BuiltInEffects.Equalizer, Q.HighGain)] = new("Agudos", "Cambiar agudos", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Equalizer, Q.HighFrequency)] = new("Frecuencia", "Cambiar frecuencia de agudos", "Hz", 1, 100, 0),

            [(BuiltInEffects.Compressor, K.Threshold)] = new("Umbral", "Cambiar umbral", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.Ratio)] = new("Relación", "Cambiar relación", ":1", 1, 0.1, 1),
            [(BuiltInEffects.Compressor, K.Attack)] = new("Ataque", "Cambiar ataque", "ms", 1, 0.5, 1, UiRange.Custom, 0.1, 100),
            [(BuiltInEffects.Compressor, K.Release)] = new("Relajación", "Cambiar relajación", "ms", 1, 5, 0, UiRange.Custom, 10, 1000),
            [(BuiltInEffects.Compressor, K.Makeup)] = new("Compensación", "Cambiar compensación", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.Knee)] = new("Rodilla", "Cambiar rodilla", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.Mix)] = new("Mezcla", "Cambiar mezcla en paralelo", "%", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.Lookahead)] = new("Anticipación", "Cambiar anticipación", "ms", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.Detector)] = new("Detector RMS", "Cambiar detector", "", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.RmsWindow)] = new("Ventana RMS", "Cambiar ventana RMS", "ms", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.AutoRelease)] = new("Relajación automática", "Cambiar relajación automática", "", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.AutoMakeup)] = new("Compensación automática", "Cambiar compensación automática", "", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.StereoLink)] = new("Estéreo enlazado", "Cambiar enlace estéreo", "", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.SidechainHighPass)] = new("Pasa altos del detector", "Cambiar pasa altos del detector", "Hz", 1, 5, 0),
            [(BuiltInEffects.Compressor, K.Bands)] = new("Bandas", "Cambiar bandas", "", 1, 2, 0),
            [(BuiltInEffects.Compressor, K.LowCrossover)] = new("Cruce graves-medios", "Cambiar cruce graves-medios", "Hz", 1, 10, 0),
            [(BuiltInEffects.Compressor, K.HighCrossover)] = new("Cruce medios-agudos", "Cambiar cruce medios-agudos", "Hz", 1, 50, 0),
            [(BuiltInEffects.Compressor, K.LowThreshold)] = new("Umbral de graves", "Cambiar umbral de graves", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.MidThreshold)] = new("Umbral de medios", "Cambiar umbral de medios", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.HighThreshold)] = new("Umbral de agudos", "Cambiar umbral de agudos", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.LowGain)] = new("Ganancia de graves", "Cambiar ganancia de graves", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.MidGain)] = new("Ganancia de medios", "Cambiar ganancia de medios", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.HighGain)] = new("Ganancia de agudos", "Cambiar ganancia de agudos", "dB", 1, 0.5, 1),
            [(BuiltInEffects.Compressor, K.Limiter)] = new("Limitador", "Cambiar limitador", "", 1, 1, 0),
            [(BuiltInEffects.Compressor, K.Ceiling)] = new("Techo", "Cambiar techo del limitador", "dBTP", 1, 0.1, 1),

            [(BuiltInEffects.Stabilize, BuiltInEffects.StabilizeParameters.Intensity)] =
                Percent with { Label = "Intensidad", Description = "Cambiar intensidad de estabilizar" },

            [(BuiltInEffects.Mask, Mk.X)] = new("Centro X", "Mover la máscara", "%", 100, 0.5, 1, UiRange.Custom, -50, 150),
            [(BuiltInEffects.Mask, Mk.Y)] = new("Centro Y", "Mover la máscara", "%", 100, 0.5, 1, UiRange.Custom, -50, 150),
            [(BuiltInEffects.Mask, Mk.Width)] = new("Ancho", "Cambiar ancho de la máscara", "%", 100, 0.5, 1, UiRange.Custom, 0, 200),
            [(BuiltInEffects.Mask, Mk.Height)] = new("Alto", "Cambiar alto de la máscara", "%", 100, 0.5, 1, UiRange.Custom, 0, 200),
            [(BuiltInEffects.Mask, Mk.Rotation)] = new("Rotación", "Cambiar la rotación de la máscara", "°", 1, 1, 1, UiRange.Custom, -180, 180),
            [(BuiltInEffects.Mask, Mk.Feather)] = new("Borde suave", "Cambiar borde de la máscara", "%", 100, 0.5, 1, UiRange.Custom, 0, 50),
            [(BuiltInEffects.Mask, Mk.Opacity)] = Percent with { Label = "Opacidad", Description = "Cambiar la opacidad de la máscara" },
            [(BuiltInEffects.Mask, Mk.Expansion)] = new("Expansión", "Agrandar o achicar la máscara", "%", 100, 0.25, 1, UiRange.Custom, -20, 20),
            [(BuiltInEffects.Mask, Mk.Roundness)] = Percent with { Label = "Esquinas", Description = "Redondear las esquinas de la máscara" },
            [(BuiltInEffects.Mask, Mk.Thickness)] = new("Grosor", "Cambiar el grosor del trazo", "%", 100, 0.1, 1, UiRange.Custom, 0, 20),

            [(BuiltInEffects.Pixelate, BuiltInEffects.PixelateParameters.Size)] = new("Tamaño", "Cambiar el tamaño de los bloques", "px", 1, 1, 0),

            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.TopLeftX)] = new("Arriba izq. X", "Mover una esquina", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.TopLeftY)] = new("Arriba izq. Y", "Mover una esquina", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.TopRightX)] = new("Arriba der. X", "Mover una esquina", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.TopRightY)] = new("Arriba der. Y", "Mover una esquina", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.BottomRightX)] = new("Abajo der. X", "Mover una esquina", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.BottomRightY)] = new("Abajo der. Y", "Mover una esquina", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.BottomLeftX)] = new("Abajo izq. X", "Mover una esquina", "px", Range: UiRange.SequenceWidth),
            [(BuiltInEffects.CornerPin, BuiltInEffects.CornerPinParameters.BottomLeftY)] = new("Abajo izq. Y", "Mover una esquina", "px", Range: UiRange.SequenceHeight),
            [(BuiltInEffects.Track, BuiltInEffects.TrackParameters.Threshold)] = Percent with { Label = "Umbral de confianza", Description = "Cambiar el umbral de confianza" },

            [(BuiltInEffects.Crop, Cr.Left)] = Percent with { Label = "Izquierda", Description = "Recortar a la izquierda", Step = 0.5, Decimals = 1 },
            [(BuiltInEffects.Crop, Cr.Top)] = Percent with { Label = "Arriba", Description = "Recortar arriba", Step = 0.5, Decimals = 1 },
            [(BuiltInEffects.Crop, Cr.Right)] = Percent with { Label = "Derecha", Description = "Recortar a la derecha", Step = 0.5, Decimals = 1 },
            [(BuiltInEffects.Crop, Cr.Bottom)] = Percent with { Label = "Abajo", Description = "Recortar abajo", Step = 0.5, Decimals = 1 },

            [(BuiltInEffects.ChromaKey, Ck.Red)] = Percent with { Label = "Clave rojo", Description = "Cambiar el color clave" },
            [(BuiltInEffects.ChromaKey, Ck.Green)] = Percent with { Label = "Clave verde", Description = "Cambiar el color clave" },
            [(BuiltInEffects.ChromaKey, Ck.Blue)] = Percent with { Label = "Clave azul", Description = "Cambiar el color clave" },
            [(BuiltInEffects.ChromaKey, Ck.Tolerance)] = Percent with { Label = "Tolerancia", Description = "Cambiar la tolerancia del croma" },
            [(BuiltInEffects.ChromaKey, Ck.Softness)] = Percent with { Label = "Suavidad", Description = "Cambiar la suavidad del croma" },
            [(BuiltInEffects.ChromaKey, Ck.Spill)] = Percent with { Label = "Quitar derrame", Description = "Cambiar el derrame del croma" },
            [(BuiltInEffects.ChromaKey, Ck.Cleanup)] = Percent with { Label = "Limpiar borde", Description = "Cambiar la limpieza del borde" },

            [(BuiltInEffects.ColorWheels, Wh.LiftRed)] = Percent with { Label = "Sombras rojo", Description = "Cambiar el tinte de las sombras" },
            [(BuiltInEffects.ColorWheels, Wh.LiftGreen)] = Percent with { Label = "Sombras verde", Description = "Cambiar el tinte de las sombras" },
            [(BuiltInEffects.ColorWheels, Wh.LiftBlue)] = Percent with { Label = "Sombras azul", Description = "Cambiar el tinte de las sombras" },
            [(BuiltInEffects.ColorWheels, Wh.Lift)] = Percent with { Label = "Sombras", Description = "Cambiar las sombras" },
            [(BuiltInEffects.ColorWheels, Wh.GammaRed)] = Percent with { Label = "Medios rojo", Description = "Cambiar el tinte de los medios" },
            [(BuiltInEffects.ColorWheels, Wh.GammaGreen)] = Percent with { Label = "Medios verde", Description = "Cambiar el tinte de los medios" },
            [(BuiltInEffects.ColorWheels, Wh.GammaBlue)] = Percent with { Label = "Medios azul", Description = "Cambiar el tinte de los medios" },
            [(BuiltInEffects.ColorWheels, Wh.Gamma)] = Percent with { Label = "Medios", Description = "Cambiar los medios" },
            [(BuiltInEffects.ColorWheels, Wh.GainRed)] = Percent with { Label = "Luces rojo", Description = "Cambiar el tinte de las luces" },
            [(BuiltInEffects.ColorWheels, Wh.GainGreen)] = Percent with { Label = "Luces verde", Description = "Cambiar el tinte de las luces" },
            [(BuiltInEffects.ColorWheels, Wh.GainBlue)] = Percent with { Label = "Luces azul", Description = "Cambiar el tinte de las luces" },
            [(BuiltInEffects.ColorWheels, Wh.Gain)] = Percent with { Label = "Luces", Description = "Cambiar las luces" },

            [(Tr.Dip, Tr.DipParameters.Red)] = Percent with { Label = "Rojo", Description = "Cambiar color del fundido" },
            [(Tr.Dip, Tr.DipParameters.Green)] = Percent with { Label = "Verde", Description = "Cambiar color del fundido" },
            [(Tr.Dip, Tr.DipParameters.Blue)] = Percent with { Label = "Azul", Description = "Cambiar color del fundido" },
            [(Tr.Wipe, Tr.WipeParameters.Angle)] = new("Ángulo", "Cambiar ángulo del barrido", "°", 1, 1, 0),
            [(Tr.Wipe, Tr.WipeParameters.Softness)] = Percent with { Label = "Borde suave", Description = "Cambiar borde del barrido" },
            [(Tr.ZoomDissolve, Tr.ZoomDissolveParameters.Zoom)] =
                new("Zoom", "Cambiar acercamiento", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(Tr.BlurDissolve, Tr.BlurDissolveParameters.Radius)] =
                new("Radio", "Cambiar desenfoque de la transición", "px", 1, 1, 0, UiRange.Custom, 0, 200),
            [(Tr.Wipe, Tp.BorderWidth)] = Percent with { Label = "Borde de color", Description = "Cambiar el borde de color", Step = 0.25, Decimals = 1 },
            [(Tr.Luma, Tp.BorderWidth)] = Percent with { Label = "Borde de color", Description = "Cambiar el borde de color", Step = 0.25, Decimals = 1 },
            [(Tr.Luma, Tp.Softness)] = Percent with { Label = "Borde suave", Description = "Cambiar el borde de la disolución" },
            [(Tr.LightWipe, Tp.Softness)] = Percent with { Label = "Borde suave", Description = "Cambiar el borde del barrido" },
            [(Tr.LightWipe, Tr.LightWipeParameters.Width)] = Percent with { Label = "Ancho de la luz", Description = "Cambiar el ancho de la luz" },
            [(Tr.LightWipe, Tp.Intensity)] = new("Intensidad", "Cambiar la intensidad de la luz", "%", 100, 5, 0, UiRange.Custom, 0, 400),
            [(Tr.Flash, Tp.Intensity)] = new("Intensidad", "Cambiar la intensidad del destello", "%", 100, 5, 0, UiRange.Custom, 0, 400),
            [(Tr.Flash, Tr.FlashParameters.Width)] = Percent with { Label = "Duración del destello", Description = "Cambiar la duración del destello" },
            [(Tr.Push, Tp.MotionBlur)] = Percent with { Label = "Desenfoque de movimiento", Description = "Cambiar el desenfoque de movimiento" },
            [(Tr.Slide, Tp.MotionBlur)] = Percent with { Label = "Desenfoque de movimiento", Description = "Cambiar el desenfoque de movimiento" },
            [(Tr.Spin, Tp.MotionBlur)] = Percent with { Label = "Desenfoque de movimiento", Description = "Cambiar el desenfoque de movimiento" },
            [(Tr.Bounce, Tp.MotionBlur)] = Percent with { Label = "Desenfoque de movimiento", Description = "Cambiar el desenfoque de movimiento" },
            [(Tr.Slide, Tr.SlideParameters.Parallax)] = Percent with { Label = "Arrastre del que sale", Description = "Cambiar cuánto acompaña el clip que sale" },
            [(Tr.ZoomBlur, Tr.ZoomBlurParameters.Strength)] = new("Fuerza", "Cambiar la fuerza del zoom", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(Tr.ZoomBlur, Tr.ZoomBlurParameters.CenterX)] = Percent with { Label = "Centro X", Description = "Mover el centro del zoom" },
            [(Tr.ZoomBlur, Tr.ZoomBlurParameters.CenterY)] = Percent with { Label = "Centro Y", Description = "Mover el centro del zoom" },
            [(Tr.Spin, Tr.SpinParameters.Angle)] = new("Vueltas", "Cambiar la rotación", "°", 1, 15, 0, UiRange.Custom, -1080, 1080),
            [(Tr.Spin, Tr.SpinParameters.Zoom)] = new("Zoom", "Cambiar el acercamiento durante la rotación", "%", 100, 1, 0, UiRange.Custom, 0, 200),
            [(Tr.Glitch, Tr.GlitchParameters.Amount)] = Percent with { Label = "Intensidad", Description = "Cambiar la intensidad del fallo" },
            [(Tr.Glitch, Tr.GlitchParameters.Bands)] = new("Franjas", "Cambiar las franjas", "", 1, 1, 0),
            [(Tr.Glitch, Tr.GlitchParameters.Split)] = new("Separación de color", "Cambiar la separación de los canales", "%", 100, 0.1, 1),
            [(Tr.Glitch, Tp.Seed)] = new("Variante", "Cambiar la variante", "", 1, 1, 0, UiRange.Custom, 0, 100),
            [(Tr.FilmBurn, Tr.FilmBurnParameters.Scale)] = new("Tamaño de las manchas", "Cambiar el tamaño de las manchas", "%", 100, 1, 0),
            [(Tr.FilmBurn, Tp.Seed)] = new("Variante", "Cambiar la variante", "", 1, 1, 0, UiRange.Custom, 0, 100),
            [(Tr.Bounce, Tr.BounceParameters.Bounce)] = Percent with { Label = "Rebote", Description = "Cambiar el rebote" },
            [(Tr.Slide, Tp.Angle)] = new("Ángulo", "Cambiar la dirección", "°", 1, 1, 0),
            [(Tr.LightWipe, Tp.Angle)] = new("Ángulo", "Cambiar la dirección", "°", 1, 1, 0),
            [(Tr.Luma, Tp.Angle)] = new("Ángulo", "Cambiar la dirección", "°", 1, 1, 0),
            [(Tr.Wipe, Tp.BorderRed)] = Percent with { Label = "Borde rojo", Description = "Cambiar el color del borde" },
            [(Tr.Wipe, Tp.BorderGreen)] = Percent with { Label = "Borde verde", Description = "Cambiar el color del borde" },
            [(Tr.Wipe, Tp.BorderBlue)] = Percent with { Label = "Borde azul", Description = "Cambiar el color del borde" },
            [(Tr.Luma, Tp.BorderRed)] = Percent with { Label = "Borde rojo", Description = "Cambiar el color del borde" },
            [(Tr.Luma, Tp.BorderGreen)] = Percent with { Label = "Borde verde", Description = "Cambiar el color del borde" },
            [(Tr.Luma, Tp.BorderBlue)] = Percent with { Label = "Borde azul", Description = "Cambiar el color del borde" },
            [(Tr.LightWipe, Tp.Red)] = Percent with { Label = "Rojo", Description = "Cambiar el color de la luz" },
            [(Tr.LightWipe, Tp.Green)] = Percent with { Label = "Verde", Description = "Cambiar el color de la luz" },
            [(Tr.LightWipe, Tp.Blue)] = Percent with { Label = "Azul", Description = "Cambiar el color de la luz" },
            [(Tr.FilmBurn, Tp.Red)] = Percent with { Label = "Rojo", Description = "Cambiar el color de la luz" },
            [(Tr.FilmBurn, Tp.Green)] = Percent with { Label = "Verde", Description = "Cambiar el color de la luz" },
            [(Tr.FilmBurn, Tp.Blue)] = Percent with { Label = "Azul", Description = "Cambiar el color de la luz" },
            [(Tr.Flash, Tp.Red)] = Percent with { Label = "Rojo", Description = "Cambiar el color de la luz" },
            [(Tr.Flash, Tp.Green)] = Percent with { Label = "Verde", Description = "Cambiar el color de la luz" },
            [(Tr.Flash, Tp.Blue)] = Percent with { Label = "Azul", Description = "Cambiar el color de la luz" },
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

    /// <summary>Degradados de fábrica de <see cref="Tr.Luma"/>, en el orden del registro.</summary>
    public static IReadOnlyList<(string Value, string Name)> LumaGradients { get; } =
    [
        ("linear", "Lineal"),
        ("radial", "Radial"),
        ("angular", "Reloj"),
        ("diamond", "Rombo"),
        ("noise", "Nubes"),
        ("bars", "Persianas"),
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
        Tr.LumaOptions.Gradient => LumaGradients.FirstOrDefault(d => d.Value == value).Name ?? value,
        BuiltInEffects.StabilizeOptions.Crop => value == "auto" ? "Automático" : "Sin recorte",
        BuiltInEffects.MaskOptions.Shape => MaskShapeName(value),
        BuiltInEffects.MaskOptions.Invert => value == "on" ? "Sí" : "No",
        BuiltInEffects.MaskOptions.Mode => MaskModeName(value),
        BuiltInEffects.CropOptions.Aspect => value == "free" ? "Libre" : value,
        BuiltInEffects.TransformOptions.FlipX or BuiltInEffects.TransformOptions.FlipY => value == "on" ? "Sí" : "No",
        BuiltInEffects.BlurOptions.Kind => value switch
        {
            "lens" => "De lente",
            "directional" => "Direccional",
            "radial" => "Radial (zoom)",
            _ => "Gaussiano",
        },
        BuiltInEffects.RegionOptions.Region => value switch
        {
            "inside" => "Dentro de las máscaras de efectos",
            "outside" => "Fuera de las máscaras de efectos",
            _ => "Toda la imagen",
        },
        BuiltInEffects.MaskOptions.Use => value == "effects" ? "Limitar efectos" : "Recortar la capa",
        BuiltInEffects.Reframe360Options.Projection => value == "stereographic" ? "Pequeño planeta" : "Cámara normal",
        BuiltInEffects.MirrorOptions.Mode => value switch
        {
            "left" => "Izquierda a la derecha",
            "right" => "Derecha a la izquierda",
            "top" => "Arriba hacia abajo",
            "bottom" => "Abajo hacia arriba",
            "quad" => "Cuatro cuadrantes",
            "tiles" => "Mosaico",
            "radial" => "Radial",
            "lateral" => "Lateral",
            _ => value,
        },
        _ => value,
    };

    public static string OptionLabel(string option) => option switch
    {
        BuiltInEffects.BlendOptions.Mode => "Modo de mezcla",
        BuiltInEffects.ColorOptions.Lut => "LUT",
        Tr.PushOptions.Direction => "Dirección",
        Tr.LumaOptions.Gradient => "Degradado",
        Tr.LumaOptions.Image => "Imagen",
        Tr.PresetOptions.Preset => "Transición",
        BuiltInEffects.StabilizeOptions.Crop => "Recorte",
        BuiltInEffects.MaskOptions.Shape => "Forma",
        BuiltInEffects.MaskOptions.Invert => "Invertir",
        BuiltInEffects.MaskOptions.Mode => "Combinar",
        BuiltInEffects.CropOptions.Aspect => "Proporción",
        BuiltInEffects.TransformOptions.FlipX => "Voltear en horizontal",
        BuiltInEffects.TransformOptions.FlipY => "Voltear en vertical",
        BuiltInEffects.BlurOptions.Kind => "Tipo",
        BuiltInEffects.RegionOptions.Region => "Dónde",
        BuiltInEffects.MaskOptions.Use => "Uso",
        BuiltInEffects.MirrorOptions.Mode => "Modo",
        BuiltInEffects.Reframe360Options.Projection => "Proyección",
        _ => option,
    };

    public static string MaskShapeName(string shape) => shape switch
    {
        "ellipse" => "Elipse",
        "path" => "Trazo libre",
        "image" => "Sujeto (Quitar fondo)",
        "bezier" => "Bézier",
        "stroke" => "Pluma de trazo",
        _ => "Rectángulo",
    };

    /// <summary>Cómo se combina una máscara con las anteriores.</summary>
    public static string MaskModeName(string mode) => mode switch
    {
        "add" => "Sumar",
        "subtract" => "Restar",
        "difference" => "Diferencia",
        _ => "Intersecar",
    };
}
