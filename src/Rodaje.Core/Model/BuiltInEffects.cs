namespace Rodaje.Core.Model;

/// <summary>
/// Nombres de los efectos que el motor entiende sin paquetes y de sus parámetros y opciones. Los parámetros se evalúan
/// en tiempo del origen; los valores por defecto, rangos y códigos están en <see cref="Effects.EffectRegistry"/>.
/// </summary>
public static class BuiltInEffects
{
    /// <summary>Posición, escala, rotación y opacidad de la capa. Sin este efecto la capa llena el cuadro.</summary>
    public const string Transform = "rodaje.transform";

    /// <summary>Parámetros de <see cref="Transform"/> y su valor cuando faltan.</summary>
    public static class TransformParameters
    {
        /// <summary>Desplazamiento horizontal del centro, en píxeles de la secuencia. Por defecto 0.</summary>
        public const string X = "x";

        /// <summary>Desplazamiento vertical del centro, en píxeles de la secuencia (positivo hacia abajo). Por defecto 0.</summary>
        public const string Y = "y";

        /// <summary>Escala sobre el tamaño ajustado al cuadro (1 = encaja completa). Por defecto 1.</summary>
        public const string Scale = "scale";

        /// <summary>Rotación en grados, sentido horario. Por defecto 0.</summary>
        public const string Rotation = "rotation";

        /// <summary>Opacidad de 0 a 1. Por defecto 1.</summary>
        public const string Opacity = "opacity";

        /// <summary>Valor de un parámetro que falta.</summary>
        public static double DefaultValue(string name) => name is Scale or Opacity ? 1 : 0;
    }

    /// <summary>Volumen y fundidos de un clip o de una pista de audio.</summary>
    public const string Audio = "rodaje.audio";

    /// <summary>Parámetros de <see cref="Audio"/>.</summary>
    public static class AudioParameters
    {
        /// <summary>Ganancia en decibelios, animable. Por defecto 0; en el mínimo (−60 dB) es silencio.</summary>
        public const string GainDb = "gainDb";

        /// <summary>Duración del fundido de entrada (potencia constante), en segundos. Por defecto 0.</summary>
        public const string FadeIn = "fadeIn";

        /// <summary>Duración del fundido de salida, en segundos. Por defecto 0.</summary>
        public const string FadeOut = "fadeOut";
    }

    /// <summary>Corrección de color y LUT.</summary>
    public const string Color = "rodaje.color";

    /// <summary>Parámetros de <see cref="Color"/>: todos valen 0 sin cambio, salvo <see cref="LutIntensity"/>.</summary>
    public static class ColorParameters
    {
        /// <summary>Exposición en pasos (EV), de −4 a 4.</summary>
        public const string Exposure = "exposure";

        public const string Contrast = "contrast";

        public const string Saturation = "saturation";

        /// <summary>Más cálido (positivo) o más frío.</summary>
        public const string Temperature = "temperature";

        /// <summary>Hacia magenta (positivo) o hacia verde.</summary>
        public const string Tint = "tint";

        public const string Highlights = "highlights";

        public const string Shadows = "shadows";

        /// <summary>Cuánto se aplica la LUT, de 0 a 1. Por defecto 1.</summary>
        public const string LutIntensity = "lutIntensity";
    }

    /// <summary>Opciones de <see cref="Color"/>.</summary>
    public static class ColorOptions
    {
        /// <summary>Referencia <c>paquete@versión/elemento</c> de una LUT <c>.cube</c>; vacía, sin LUT.</summary>
        public const string Lut = "lut";
    }

    public const string Vignette = "rodaje.vignette";

    public static class VignetteParameters
    {
        /// <summary>Cuánto oscurece los bordes, de 0 a 1.</summary>
        public const string Amount = "amount";

        /// <summary>Tamaño de la zona sin oscurecer, de 0 a 1.</summary>
        public const string Size = "size";

        /// <summary>Suavidad del borde, de 0 a 1.</summary>
        public const string Softness = "softness";
    }

    public const string Blur = "rodaje.blur";

    public static class BlurParameters
    {
        /// <summary>Radio en píxeles de la secuencia (la vista previa a media resolución lo escala).</summary>
        public const string Radius = "radius";
    }

    public const string Sharpen = "rodaje.sharpen";

    public static class SharpenParameters
    {
        public const string Amount = "amount";

        /// <summary>Radio en píxeles de la secuencia.</summary>
        public const string Radius = "radius";
    }

    /// <summary>Modo de mezcla de la capa con lo que tiene debajo.</summary>
    public const string Blend = "rodaje.blend";

    public static class BlendOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.BlendModes"/>. Por defecto <c>normal</c>.</summary>
        public const string Mode = "blendMode";
    }

    /// <summary>Reservado y oculto: la velocidad es <see cref="Clip.Speed"/> (formato 4) y la migración 3 → 4 quita sus instancias.</summary>
    public const string Speed = "rodaje.speed";

    /// <summary>Fuera de la fase 5: necesita análisis (fase 7).</summary>
    public const string Stabilize = "rodaje.stabilize";

    /// <summary>Opciones que cualquier efecto puede llevar además de las suyas.</summary>
    public static class CommonOptions
    {
        /// <summary>Referencia <c>paquete@versión/elemento</c> del filtro o efecto de paquete del que salió la copia.</summary>
        public const string Source = "source";
    }
}

/// <summary>Tipos de las transiciones propias del motor y sus parámetros.</summary>
public static class BuiltInTransitions
{
    /// <summary>Fundido cruzado. Es también la que se usa cuando falta el paquete de una transición.</summary>
    public const string Crossfade = "rodaje.crossfade";

    /// <summary>Fundido a un color (negro por defecto) y desde él.</summary>
    public const string Dip = "rodaje.dip";

    /// <summary>Barrido con un borde que avanza.</summary>
    public const string Wipe = "rodaje.wipe";

    /// <summary>El clip que entra empuja al que sale.</summary>
    public const string Push = "rodaje.push";

    /// <summary>Fundido con un acercamiento.</summary>
    public const string ZoomDissolve = "rodaje.zoom-dissolve";

    /// <summary>Fundido a través de un desenfoque.</summary>
    public const string BlurDissolve = "rodaje.blur-dissolve";

    /// <summary>Transición de paquete: curvas de transformación de salida y de entrada más una mezcla (<c>rodaje.preset</c>).</summary>
    public const string Preset = "rodaje.preset";

    public static class DipParameters
    {
        public const string Red = "red";

        public const string Green = "green";

        public const string Blue = "blue";
    }

    public static class WipeParameters
    {
        /// <summary>Dirección del barrido en grados; 0 avanza de izquierda a derecha.</summary>
        public const string Angle = "angle";

        /// <summary>Ancho del borde suave, de 0 a 1 del cuadro.</summary>
        public const string Softness = "softness";
    }

    public static class PushOptions
    {
        /// <summary><c>left</c>, <c>right</c>, <c>up</c> o <c>down</c>: hacia dónde sale el clip que se va.</summary>
        public const string Direction = "direction";
    }

    public static class ZoomDissolveParameters
    {
        /// <summary>Acercamiento máximo a mitad de la transición: 0,25 es un 25 % más grande.</summary>
        public const string Zoom = "zoom";
    }

    public static class BlurDissolveParameters
    {
        /// <summary>Radio máximo a mitad de la transición, en píxeles de la secuencia.</summary>
        public const string Radius = "radius";
    }

    public static class PresetOptions
    {
        /// <summary>Referencia <c>paquete@versión/elemento</c> del preajuste.</summary>
        public const string Preset = "preset";
    }
}
