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

    /// <summary>Ecualizador paramétrico de cinco bandas de un clip o de una pista de audio (sin keyframes).</summary>
    public const string Equalizer = "rodaje.equalizer";

    /// <summary>Parámetros de <see cref="Equalizer"/>: ganancias en dB (0 = banda apagada), frecuencias en Hz.</summary>
    public static class EqualizerParameters
    {
        /// <summary>Corte de graves (pasa altos de 12 dB por octava) en Hz; 0 lo apaga.</summary>
        public const string LowCut = "lowCut";

        public const string LowGain = "lowGain";

        public const string LowFrequency = "lowFrequency";

        public const string MidGain = "midGain";

        public const string MidFrequency = "midFrequency";

        public const string MidQ = "midQ";

        public const string PresenceGain = "presenceGain";

        public const string PresenceFrequency = "presenceFrequency";

        public const string PresenceQ = "presenceQ";

        public const string HighGain = "highGain";

        public const string HighFrequency = "highFrequency";
    }

    /// <summary>Compresor de un clip o de una pista de audio, después del ecualizador (sin keyframes).</summary>
    public const string Compressor = "rodaje.compressor";

    public static class CompressorParameters
    {
        /// <summary>Umbral en dBFS.</summary>
        public const string Threshold = "threshold";

        /// <summary>Relación (4 = 4:1).</summary>
        public const string Ratio = "ratio";

        /// <summary>Ataque en milisegundos.</summary>
        public const string Attack = "attack";

        /// <summary>Relajación en milisegundos.</summary>
        public const string Release = "release";

        /// <summary>Ganancia de compensación en dB.</summary>
        public const string Makeup = "makeup";

        /// <summary>Ancho de la rodilla suave en dB.</summary>
        public const string Knee = "knee";
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

    /// <summary>
    /// Quita el temblor de la cámara con el análisis de movimiento guardado en la caché (fase 7). La corrección de cada
    /// cuadro va antes de la transformación.
    /// </summary>
    public const string Stabilize = "rodaje.stabilize";

    public static class StabilizeParameters
    {
        /// <summary>De 0 (sin corrección) a 1 (trayectoria lo más suave). Por defecto 0,5. No se anima.</summary>
        public const string Intensity = "intensity";
    }

    public static class StabilizeOptions
    {
        /// <summary><c>auto</c> acerca lo justo para no ver el borde; <c>off</c> deja ver el borde que mueve la corrección.</summary>
        public const string Crop = "crop";

        /// <summary>
        /// Clave del análisis de movimiento en la caché (la pone «Estabilizar» al analizar). Sin ella, o si falta en la caché,
        /// el efecto no corrige nada. No está en el registro: se guarda y se valida como texto libre.
        /// </summary>
        public const string Analysis = "analysis";
    }

    /// <summary>
    /// Máscara de la capa: rectángulo, elipse, trazo libre o imagen (un video gris en <c>&lt;proyecto&gt;.medios</c>). Varias
    /// máscaras se multiplican. La geometría va en coordenadas de la imagen de la capa (0–1) y se calcula con
    /// <see cref="Effects.MaskGeometry"/>.
    /// </summary>
    public const string Mask = "rodaje.mask";

    public static class MaskParameters
    {
        /// <summary>Centro horizontal, de 0 (borde izquierdo de la imagen) a 1. Por defecto 0,5.</summary>
        public const string X = "x";

        /// <summary>Centro vertical, de 0 (arriba) a 1. Por defecto 0,5.</summary>
        public const string Y = "y";

        /// <summary>Ancho como fracción del ancho de la imagen. Por defecto 0,5.</summary>
        public const string Width = "width";

        /// <summary>Alto como fracción del alto de la imagen. Por defecto 0,5.</summary>
        public const string Height = "height";

        /// <summary>Giro en grados, sentido horario.</summary>
        public const string Rotation = "rotation";

        /// <summary>Ancho del borde suave como fracción del lado corto de la imagen, de 0 a 1.</summary>
        public const string Feather = "feather";

        /// <summary>Cuánto se aplica, de 0 a 1. Por defecto 1.</summary>
        public const string Opacity = "opacity";
    }

    public static class MaskOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.MaskShapes"/>.</summary>
        public const string Shape = "shape";

        /// <summary><c>off</c> o <c>on</c>: se ve lo de afuera de la forma.</summary>
        public const string Invert = "invert";

        /// <summary>Puntos del trazo libre relativos a la caja de la máscara (<see cref="Effects.MaskGeometry.FormatPath"/>).</summary>
        public const string Path = "path";

        /// <summary><c>file:</c> más la ruta del video gris de la máscara de imagen, relativa a la carpeta del proyecto.</summary>
        public const string Matte = "matte";

        /// <summary>Instante del origen del clip (en segundos, cultura invariante) que corresponde al primer cuadro del video gris.</summary>
        public const string MatteStart = "matteStart";
    }

    /// <summary>
    /// Recorte de la imagen de la capa: lo que queda se ajusta al cuadro como si fuera la imagen entera y las máscaras van
    /// sobre esa parte. La proporción fija la imponen la interfaz y <see cref="Effects.CropGeometry"/>; el motor solo lee los bordes.
    /// </summary>
    public const string Crop = "rodaje.crop";

    public static class CropParameters
    {
        /// <summary>Lo que se quita a la izquierda, en fracción del ancho de la imagen. Por defecto 0.</summary>
        public const string Left = "left";

        public const string Top = "top";

        public const string Right = "right";

        public const string Bottom = "bottom";
    }

    public static class CropOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.CropAspects"/>: <c>free</c> o una proporción fija del recorte.</summary>
        public const string Aspect = "aspect";
    }

    /// <summary>
    /// Croma (pantalla verde o azul): vuelve transparente lo que se parece al color clave, en Y'CbCr BT.709. Va antes del
    /// color y de la LUT, sobre la imagen original, y se multiplica con las máscaras.
    /// </summary>
    public const string ChromaKey = "rodaje.chromakey";

    public static class ChromaKeyParameters
    {
        /// <summary>Color clave, de 0 a 1 en R'G'B'. No se anima.</summary>
        public const string Red = "keyRed";

        public const string Green = "keyGreen";

        public const string Blue = "keyBlue";

        /// <summary>Distancia de croma por debajo de la cual el píxel es transparente, de 0 a 1.</summary>
        public const string Tolerance = "tolerance";

        /// <summary>Ancho de la transición a opaco, de 0 a 1.</summary>
        public const string Softness = "softness";

        /// <summary>Cuánto se quita del tono de la clave en lo que queda (derrame), de 0 a 1.</summary>
        public const string Spill = "spill";

        /// <summary>Recorta los semitransparentes del borde por arriba y por abajo, de 0 a 1.</summary>
        public const string Cleanup = "cleanup";
    }

    /// <summary>Ruedas de color: sombras (lift), medios (gamma) y luces (gain), cada una con un tinte R'G'B' y un valor general.</summary>
    public const string ColorWheels = "rodaje.wheels";

    public static class ColorWheelsParameters
    {
        public const string LiftRed = "liftRed";
        public const string LiftGreen = "liftGreen";
        public const string LiftBlue = "liftBlue";

        /// <summary>Sombras: <c>c + lift·(1 − c)</c>; sube o baja el negro sin mover el blanco.</summary>
        public const string Lift = "lift";

        public const string GammaRed = "gammaRed";
        public const string GammaGreen = "gammaGreen";
        public const string GammaBlue = "gammaBlue";

        /// <summary>Medios: <c>c^(2^−gamma)</c>; positivo aclara.</summary>
        public const string Gamma = "gamma";

        public const string GainRed = "gainRed";
        public const string GainGreen = "gainGreen";
        public const string GainBlue = "gainBlue";

        /// <summary>Luces: <c>c·(1 + gain)</c>.</summary>
        public const string Gain = "gain";
    }

    /// <summary>Curvas: una general y una por canal, con puntos que se unen con una cúbica monótona (<see cref="Effects.ToneCurve"/>).</summary>
    public const string Curves = "rodaje.curves";

    public static class CurvesOptions
    {
        /// <summary>Puntos <c>x,y;x,y;…</c> de 0 a 1; vacía es la identidad. La general va después de la del canal.</summary>
        public const string Master = "master";

        public const string Red = "red";

        public const string Green = "green";

        public const string Blue = "blue";
    }

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
