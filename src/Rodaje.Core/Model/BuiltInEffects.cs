namespace Rodaje.Core.Model;

/// <summary>Efectos que el motor entiende sin paquetes. Sus parámetros se evalúan en tiempo del origen.</summary>
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

    /// <summary>Volumen y fundidos de un clip de audio. El mezclador todavía no los aplica (fase 5).</summary>
    public const string Audio = "rodaje.audio";

    /// <summary>Parámetros de <see cref="Audio"/> y su valor cuando faltan.</summary>
    public static class AudioParameters
    {
        /// <summary>Ganancia en decibelios. Por defecto 0.</summary>
        public const string GainDb = "gainDb";

        /// <summary>Duración del fundido de entrada, en segundos. Por defecto 0.</summary>
        public const string FadeIn = "fadeIn";

        /// <summary>Duración del fundido de salida, en segundos. Por defecto 0.</summary>
        public const string FadeOut = "fadeOut";
    }

    /// <summary>Corrección de color y LUT.</summary>
    public const string Color = "rodaje.color";

    public const string Blur = "rodaje.blur";

    public const string Sharpen = "rodaje.sharpen";

    public const string Vignette = "rodaje.vignette";

    /// <summary>Cambio de velocidad del clip.</summary>
    public const string Speed = "rodaje.speed";

    public const string Stabilize = "rodaje.stabilize";
}
