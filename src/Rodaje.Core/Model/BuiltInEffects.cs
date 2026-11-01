namespace Rodaje.Core.Model;

/// <summary>
/// Nombres de los efectos que el motor entiende sin paquetes y de sus parámetros y opciones. Los parámetros se evalúan
/// en tiempo del origen; los valores por defecto, rangos y códigos están en <see cref="Effects.EffectRegistry"/>.
/// </summary>
public static partial class BuiltInEffects
{
    /// <summary>Posición, escala, rotación y opacidad de la capa. Sin este efecto la capa llena el cuadro.</summary>
    public const string Transform = "rodaje.transform";

    /// <summary>Parámetros de <see cref="Transform"/> y su valor cuando faltan.</summary>
    public static class TransformParameters
    {
        public const string HdrReferenceNits = "hdrReferenceNits";

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

        /// <summary>
        /// Punto de anclaje: desde el centro de la imagen, en píxeles de la secuencia a escala 1 (positivo hacia la
        /// derecha y hacia abajo). Escala, giro e inclinación pivotan en él; mover el ancla no mueve la imagen. Por defecto 0.
        /// </summary>
        public const string AnchorX = "anchorX";

        public const string AnchorY = "anchorY";

        /// <summary>Escala por eje, que multiplica a <see cref="Scale"/> (1 = sin estirar). Por defecto 1.</summary>
        public const string ScaleX = "scaleX";

        public const string ScaleY = "scaleY";

        /// <summary>Inclinación en grados (−85 a 85): <see cref="SkewX"/> tuerce los lados verticales y <see cref="SkewY"/> los horizontales.</summary>
        public const string SkewX = "skewX";

        public const string SkewY = "skewY";

        /// <summary>
        /// Desenfoque de movimiento: ángulo del obturador en grados (0 lo apaga, 180 es el de cine, 360 todo el cuadro, hasta
        /// 720). Sin keyframes.
        /// </summary>
        public const string ShutterAngle = "shutterAngle";

        /// <summary>Corrimiento del centro del obturador respecto del cuadro, en grados (0 = centrado). Sin keyframes.</summary>
        public const string ShutterPhase = "shutterPhase";

        /// <summary>Muestras por cuadro del desenfoque de movimiento (2 a 64). Por defecto 16. Sin keyframes.</summary>
        public const string MotionSamples = "motionSamples";

        /// <summary>Valor de un parámetro que falta.</summary>
        public static double DefaultValue(string name) => name switch
        {
            Scale or Opacity or ScaleX or ScaleY => 1,
            MotionSamples => DefaultMotionSamples,
            _ => 0,
        };

        public const int DefaultMotionSamples = 16;
    }

    /// <summary>Opciones de <see cref="Transform"/>.</summary>
    public static class TransformOptions
    {
        /// <summary>Voltear en horizontal alrededor del centro de la imagen: <c>off</c> u <c>on</c>.</summary>
        public const string FlipX = "flipX";

        /// <summary>Voltear en vertical alrededor del centro de la imagen: <c>off</c> u <c>on</c>.</summary>
        public const string FlipY = "flipY";

        /// <summary>
        /// Expandir un clip SDR a HDR en una secuencia HDR (<c>off</c> u <c>on</c>): las luces suben hasta el pico de
        /// <see cref="TransformParameters.HdrReferenceNits"/> (203–1000 nits; 0 elige 1000) con la curva inversa de BT.2446 método A
        /// anclada en el gris del 18 % (<c>Effects.SdrExpansion</c>). En una secuencia SDR no hace nada.
        /// </summary>
        public const string HdrExpand = "hdrExpand";
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

        /// <summary>Paneo con ley de −3 dB, de −100 (izquierda) a 100 (derecha), animable. Por defecto 0.</summary>
        public const string Pan = "pan";

        /// <summary>Balance estéreo, de −100 (solo la izquierda) a 100 (solo la derecha), animable. Por defecto 0.</summary>
        public const string Balance = "balance";
    }

    /// <summary>Opciones de <see cref="Audio"/>.</summary>
    public static class AudioOptions
    {
        /// <summary>
        /// Curva de los fundidos de entrada y salida: uno de <see cref="Effects.EffectRegistry.FadeShapes"/>. Por defecto
        /// <c>power</c> (potencia constante, seno y coseno); <c>linear</c> es ganancia constante y <c>exponential</c>,
        /// lineal en decibelios. Una versión anterior de Rodaje la ignora y funde en potencia constante.
        /// </summary>
        public const string FadeShape = "fadeShape";
    }

    /// <summary>Valores de <see cref="AudioOptions.FadeShape"/>.</summary>
    public static class FadeShapes
    {
        public const string Power = "power";
        public const string Linear = "linear";
        public const string Exponential = "exponential";
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

    /// <summary>
    /// Compresor de un clip, de una pista de audio o de la mezcla, después del ecualizador. Umbral, relación y mezcla
    /// admiten keyframes; los interruptores valen 0 o 1.
    /// </summary>
    public const string Compressor = "rodaje.compressor";

    public static class CompressorParameters
    {
        /// <summary>Umbral en dBFS.</summary>
        public const string Threshold = "threshold";

        /// <summary>Relación (4 = 4:1); 100 es ∞:1 (limitador).</summary>
        public const string Ratio = "ratio";

        /// <summary>Ataque en milisegundos.</summary>
        public const string Attack = "attack";

        /// <summary>Relajación en milisegundos.</summary>
        public const string Release = "release";

        /// <summary>Ganancia de compensación en dB.</summary>
        public const string Makeup = "makeup";

        /// <summary>Ancho de la rodilla suave en dB.</summary>
        public const string Knee = "knee";

        /// <summary>Mezcla en paralelo en %: 0 la señal original, 100 solo la comprimida.</summary>
        public const string Mix = "mix";

        /// <summary>Anticipación del detector en ms (0 a 10); la línea de tiempo la compensa.</summary>
        public const string Lookahead = "lookahead";

        /// <summary>Detector: 0 pico, 1 RMS.</summary>
        public const string Detector = "detector";

        /// <summary>Ventana del RMS en ms.</summary>
        public const string RmsWindow = "rmsWindow";

        /// <summary>1: relajación dependiente del programa.</summary>
        public const string AutoRelease = "autoRelease";

        /// <summary>1: compensación automática según umbral y relación (se suma a <see cref="Makeup"/>).</summary>
        public const string AutoMakeup = "autoMakeup";

        /// <summary>1: estéreo enlazado (una reducción para los dos canales); 0, por canal.</summary>
        public const string StereoLink = "stereoLink";

        /// <summary>Pasa altos del detector en Hz; 0 lo apaga.</summary>
        public const string SidechainHighPass = "sidechainHighPass";

        /// <summary>Bandas: 1 o 3 (multibanda).</summary>
        public const string Bands = "bands";

        public const string LowCrossover = "lowCrossover";

        public const string HighCrossover = "highCrossover";

        /// <summary>Desplazamiento del umbral de la banda grave respecto del general, en dB.</summary>
        public const string LowThreshold = "lowThreshold";

        public const string MidThreshold = "midThreshold";

        public const string HighThreshold = "highThreshold";

        /// <summary>Ganancia de salida de la banda grave, en dB.</summary>
        public const string LowGain = "lowGain";

        public const string MidGain = "midGain";

        public const string HighGain = "highGain";

        /// <summary>1: limitador de pico verdadero al final.</summary>
        public const string Limiter = "limiter";

        /// <summary>Techo del limitador en dBTP.</summary>
        public const string Ceiling = "ceiling";
    }

    public static class CompressorOptions
    {
        /// <summary>Identificador de la pista cuya mezcla dispara el compresor (sidechain externo); sin ella, el propio audio.</summary>
        public const string Sidechain = "sidechain";
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

        /// <summary>
        /// Protección de piel, de 0 a 1: en los tonos de piel (cerca de la línea de piel del vectorscopio) deja fuera la
        /// saturación, la temperatura y el tinte en esa proporción. Por defecto 0.
        /// </summary>
        public const string SkinProtection = "skinProtection";
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

        /// <summary>0 sigue la forma del cuadro (elipse); 1 es un círculo en píxeles. Por defecto 0.</summary>
        public const string Roundness = "roundness";

        /// <summary>Centro de la viñeta en fracción del ancho de la imagen. Por defecto 0,5.</summary>
        public const string CenterX = "centerX";

        public const string CenterY = "centerY";
    }

    public const string Blur = "rodaje.blur";

    public static class BlurParameters
    {
        /// <summary>Radio en píxeles de la secuencia (la vista previa a media resolución lo escala).</summary>
        public const string Radius = "radius";

        /// <summary>Dirección del desenfoque direccional en grados; 0 es horizontal.</summary>
        public const string Angle = "angle";

        /// <summary>Centro del desenfoque radial, en fracción de la imagen. Por defecto 0,5.</summary>
        public const string CenterX = "centerX";

        public const string CenterY = "centerY";
    }

    public static class BlurOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.BlurKinds"/>: gaussiano (por defecto), de lente, direccional o radial.</summary>
        public const string Kind = "kind";
    }

    /// <summary>Opción de los efectos de imagen que se pueden limitar con las máscaras que «limitan efectos».</summary>
    public static class RegionOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.Regions"/>: en toda la imagen, dentro o fuera de esas máscaras.</summary>
        public const string Region = "region";
    }

    public const string Sharpen = "rodaje.sharpen";

    public static class SharpenParameters
    {
        public const string Amount = "amount";

        /// <summary>Radio en píxeles de la secuencia.</summary>
        public const string Radius = "radius";

        /// <summary>Umbral de la máscara de enfoque, de 0 a 0,25: las diferencias más chicas (ruido, piel) no se realzan.</summary>
        public const string Threshold = "threshold";
    }

    /// <summary>Grano de película: ruido que cambia en cada cuadro, más en los medios tonos.</summary>
    public const string Grain = "rodaje.grain";

    public static class GrainParameters
    {
        /// <summary>Intensidad, de 0 a 1.</summary>
        public const string Amount = "amount";

        /// <summary>Tamaño del grano en píxeles de la secuencia.</summary>
        public const string Size = "size";

        /// <summary>De 0 (grano gris) a 1 (grano de color).</summary>
        public const string Color = "color";

        /// <summary>De 0 (igual en todos los tonos) a 1 (sobre todo en los medios, como la película).</summary>
        public const string Response = "response";
    }

    /// <summary>Resplandor: las luces se desenfocan y se suman en luz lineal.</summary>
    public const string Glow = "rodaje.glow";

    public static class GlowParameters
    {
        /// <summary>Desde qué luminancia brilla, de 0 a 1.</summary>
        public const string Threshold = "threshold";

        /// <summary>Radio del halo en píxeles de la secuencia.</summary>
        public const string Radius = "radius";

        /// <summary>Cuánto se suma el halo, de 0 a 4.</summary>
        public const string Intensity = "intensity";
    }

    /// <summary>Aberración cromática: el rojo y el azul se separan del verde.</summary>
    public const string ChromaticAberration = "rodaje.aberration";

    public static class ChromaticAberrationParameters
    {
        /// <summary>Separación en píxeles de la secuencia (en la esquina, si es radial).</summary>
        public const string Amount = "amount";

        /// <summary>Dirección de la separación lateral, en grados.</summary>
        public const string Angle = "angle";
    }

    public static class ChromaticAberrationOptions
    {
        /// <summary><c>radial</c> (crece hacia los bordes, como una lente) o <c>lateral</c> (igual en todo el cuadro).</summary>
        public const string Mode = "mode";
    }

    /// <summary>Reducción de ruido: un filtro que respeta los bordes y, al reproducir y exportar, una mezcla con el cuadro anterior.</summary>
    public const string Denoise = "rodaje.denoise";

    public static class DenoiseParameters
    {
        /// <summary>Fuerza del filtro espacial, de 0 a 1.</summary>
        public const string Strength = "strength";

        /// <summary>Cuánto se mezcla con el cuadro anterior donde no hay movimiento, de 0 a 1.</summary>
        public const string Temporal = "temporal";

        /// <summary>Cuánto detalle se conserva, de 0 a 1: más alto deja intactos más bordes.</summary>
        public const string Detail = "detail";
    }

    /// <summary>Espejo y pantalla dividida: refleja una mitad o repite la imagen en mosaico.</summary>
    public const string Mirror = "rodaje.mirror";

    public static class MirrorParameters
    {
        /// <summary>Posición del eje del espejo, de 0 a 1 de la imagen. Por defecto 0,5.</summary>
        public const string Axis = "axis";

        /// <summary>Copias por lado en el mosaico, de 1 a 6.</summary>
        public const string Tiles = "tiles";
    }

    public static class MirrorOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.MirrorModes"/>.</summary>
        public const string Mode = "mode";
    }

    /// <summary>
    /// Reencuadre de video 360 equirectangular a una vista plana (<see cref="Effects.Equirectangular"/>). Lo trae el motor
    /// pero solo se ofrece con el complemento <c>rodaje-360</c> instalado (<see cref="Effects.EffectDefinition.Feature"/>).
    /// </summary>
    public const string Reframe360 = "rodaje.reframe360";

    public static class Reframe360Parameters
    {
        /// <summary>Guiñada en grados, de −180 a 180: positiva mira a la derecha.</summary>
        public const string Yaw = "yaw";

        /// <summary>Cabeceo en grados, de −90 a 90: positivo mira arriba.</summary>
        public const string Pitch = "pitch";

        /// <summary>Alabeo en grados, de −180 a 180: positivo gira la imagen en sentido horario.</summary>
        public const string Roll = "roll";

        /// <summary>Campo de visión horizontal en grados, de 20 a 330 (la rectilínea llega hasta 170).</summary>
        public const string Fov = "fov";
    }

    public static class Reframe360Options
    {
        /// <summary><c>rectilinear</c> (una cámara normal) o <c>stereographic</c> (pequeño planeta).</summary>
        public const string Projection = "projection";
    }

    /// <summary>Distorsión de lente: barril (positivo) o cojín (negativo).</summary>
    public const string LensDistortion = "rodaje.distortion";

    public static class LensDistortionParameters
    {
        /// <summary>De −1 (cojín) a 1 (barril).</summary>
        public const string Amount = "amount";

        /// <summary>Acercamiento para tapar los bordes, de 0,5 a 2. Por defecto 1.</summary>
        public const string Zoom = "zoom";
    }

    /// <summary>
    /// Corrección secundaria con calificador HSL: elige los píxeles por tono, saturación y luminancia (con bordes suaves) y
    /// solo a ellos les cambia tono, saturación y exposición.
    /// </summary>
    public const string HslQualifier = "rodaje.qualifier";

    public static class HslQualifierParameters
    {
        /// <summary>Tono central en grados (0 rojo, 120 verde, 240 azul).</summary>
        public const string HueCenter = "hueCenter";

        /// <summary>Medio ancho del tono elegido, en grados.</summary>
        public const string HueWidth = "hueWidth";

        /// <summary>Suavidad del borde del tono, en grados.</summary>
        public const string HueSoftness = "hueSoftness";

        public const string SaturationLow = "satLow";

        public const string SaturationHigh = "satHigh";

        public const string LumaLow = "lumaLow";

        public const string LumaHigh = "lumaHigh";

        /// <summary>Suavidad de los bordes de saturación y luminancia, de 0 a 0,5.</summary>
        public const string Softness = "softness";

        /// <summary>Corrección: giro del tono en grados.</summary>
        public const string HueShift = "hueShift";

        /// <summary>Corrección: saturación, de −1 a 1.</summary>
        public const string Saturation = "saturation";

        /// <summary>Corrección: exposición en pasos (EV).</summary>
        public const string Exposure = "exposure";
    }

    public static class HslQualifierOptions
    {
        /// <summary><c>off</c> u <c>on</c>: corrige lo que queda fuera de la selección.</summary>
        public const string Invert = "invert";
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
    /// Máscara de la capa o de un efecto de la pila (<see cref="MaskOptions.Target"/>): rectángulo (con esquinas
    /// redondeadas), elipse, trazo libre, Bézier, pluma de trazo o imagen (un video gris en <c>&lt;proyecto&gt;.medios</c>).
    /// Las máscaras de un mismo destino se combinan en orden con su <see cref="MaskOptions.Mode"/>. La geometría va en
    /// coordenadas de la imagen de la capa (0–1) y se calcula con <see cref="Effects.MaskGeometry"/>.
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

        /// <summary>Agranda (positivo) o achica el contorno, en fracción del lado corto de la imagen, de −0,5 a 0,5.</summary>
        public const string Expansion = "expansion";

        /// <summary>Radio de las esquinas del rectángulo, en fracción de la mitad de su lado corto, de 0 a 1.</summary>
        public const string Roundness = "roundness";

        /// <summary>Grosor de la pluma de trazo, en fracción del lado corto de la imagen. Por defecto 0,02.</summary>
        public const string Thickness = "thickness";
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

        /// <summary>
        /// Uno de <see cref="Effects.EffectRegistry.MaskModes"/>: cómo se combina con las anteriores del mismo destino. Sin
        /// la opción vale <c>intersect</c> (se multiplican, como antes del formato 7).
        /// </summary>
        public const string Mode = "combine";

        /// <summary>Vértices de Bézier o de la pluma de trazo (<see cref="Effects.MaskPath.Format"/>), relativos a la caja.</summary>
        public const string Points = "points";

        /// <summary>
        /// Forma animada: keyframes de los vértices en tiempo del origen (<see cref="Effects.MaskShapeTrack"/>). Si tiene
        /// alguno, manda sobre <see cref="Points"/>.
        /// </summary>
        public const string ShapeKeys = "shapeKeys";

        /// <summary>Identificador de un efecto de imagen del mismo clip: la máscara limita ese efecto en vez de la capa.</summary>
        public const string Target = "target";

        /// <summary>Nombre que ve quien edita; no cambia la imagen.</summary>
        public const string Name = "name";

        /// <summary>Color del contorno en el visor (<c>#RRGGBB</c>); no cambia la imagen.</summary>
        public const string Color = "color";

        /// <summary>
        /// Uno de <see cref="Effects.EffectRegistry.MaskUses"/>: <c>cut</c> (por defecto) recorta la capa; <c>effects</c> no
        /// recorta y marca dónde se aplican los efectos con la opción <see cref="RegionOptions.Region"/>.
        /// </summary>
        public const string Use = "use";
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

    /// <summary>Pixelar: bloques del tamaño dado, en píxeles de la secuencia. Con una máscara, tapa una zona (caras, matrículas).</summary>
    public const string Pixelate = "rodaje.pixelate";

    public static class PixelateParameters
    {
        /// <summary>Lado del bloque en píxeles de la secuencia, de 2 a 256. Por defecto 24.</summary>
        public const string Size = "size";
    }

    /// <summary>
    /// Anclaje de esquinas (formato 13): lleva cada esquina de la imagen de la capa a otro lugar con perspectiva (una
    /// homografía), antes de la transformación. Los parámetros son desplazamientos de cada esquina en píxeles de la
    /// secuencia sobre la imagen ajustada al cuadro; todos en 0 no cambian nada.
    /// </summary>
    public const string CornerPin = "rodaje.cornerpin";

    public static class CornerPinParameters
    {
        public const string TopLeftX = "topLeftX";
        public const string TopLeftY = "topLeftY";
        public const string TopRightX = "topRightX";
        public const string TopRightY = "topRightY";
        public const string BottomRightX = "bottomRightX";
        public const string BottomRightY = "bottomRightY";
        public const string BottomLeftX = "bottomLeftX";
        public const string BottomLeftY = "bottomLeftY";

        /// <summary>En el orden de las esquinas: arriba izquierda, arriba derecha, abajo derecha, abajo izquierda; x e y de cada una.</summary>
        public static readonly string[] All = [TopLeftX, TopLeftY, TopRightX, TopRightY, BottomRightX, BottomRightY, BottomLeftX, BottomLeftY];
    }

    /// <summary>
    /// Un seguimiento guardado en el clip (formato 13): no dibuja nada. Guarda los puntos que se siguieron en la imagen de la
    /// capa, cuadro a cuadro y con su confianza (<see cref="Effects.PointTrack"/>), para aplicarlos a otro clip, una máscara,
    /// un anclaje de esquinas o una estabilización.
    /// </summary>
    public const string Track = "rodaje.track";

    public static class TrackParameters
    {
        /// <summary>Confianza (0–1) por debajo de la cual un cuadro es dudoso. Por defecto 0,7.</summary>
        public const string Threshold = "threshold";
    }

    public static class TrackOptions
    {
        /// <summary>Uno de <see cref="Effects.EffectRegistry.TrackKinds"/>: un punto, dos, cuatro esquinas o una región.</summary>
        public const string Kind = "kind";

        /// <summary>Nombre que se ve en la lista.</summary>
        public const string Name = "name";

        /// <summary>
        /// Los puntos al empezar: <c>x,y,anchoPatrón,altoPatrón,anchoBúsqueda,altoBúsqueda</c> por punto, separados por
        /// <c>;</c>, todo en fracciones de la imagen de la capa.
        /// </summary>
        public const string Points = "points";

        /// <summary>Los cuadros seguidos (<see cref="Effects.PointTrack"/>): <c>ticks@x,y,c;x,y,c|…</c> en tiempo del origen.</summary>
        public const string Samples = "samples";

        /// <summary>Color de los puntos en el visor (<c>#RRGGBB</c>).</summary>
        public const string Color = "color";
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

        public const string OffsetRed = "offsetRed";
        public const string OffsetGreen = "offsetGreen";
        public const string OffsetBlue = "offsetBlue";

        /// <summary>Desplazamiento: <c>c + offset</c> antes de las demás ruedas; sube o baja toda la imagen por igual (negros incluidos).</summary>
        public const string Offset = "offset";
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

        /// <summary>Tono contra tono: puntos <c>x,y</c> con x el tono (0–1) e y el giro (0,5 sin cambio; 0 y 1, media vuelta). Periódica.</summary>
        public const string HueHue = "hueHue";

        /// <summary>Tono contra saturación: y es la mitad de la ganancia (0,5 sin cambio). Periódica.</summary>
        public const string HueSaturation = "hueSat";

        /// <summary>Luminancia contra saturación: x es la luma e y la mitad de la ganancia (0,5 sin cambio).</summary>
        public const string LumaSaturation = "lumaSat";
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

    /// <summary>El clip que entra se desliza sobre el que sale, con desenfoque de movimiento.</summary>
    public const string Slide = "rodaje.slide";

    /// <summary>Acercamiento con desenfoque radial hacia un punto y fundido en el medio.</summary>
    public const string ZoomBlur = "rodaje.zoom-blur";

    /// <summary>Giro sobre el centro con acercamiento y desenfoque de movimiento.</summary>
    public const string Spin = "rodaje.spin";

    /// <summary>Barrido con una franja de luz en el borde.</summary>
    public const string LightWipe = "rodaje.light-wipe";

    /// <summary>Fallo digital: franjas corridas, canales separados y cambio por bloques.</summary>
    public const string Glitch = "rodaje.glitch";

    /// <summary>Quemado de película: manchas de luz cálida que crecen y dejan ver el clip que entra.</summary>
    public const string FilmBurn = "rodaje.film-burn";

    /// <summary>Destello de luz que tapa el corte.</summary>
    public const string Flash = "rodaje.flash";

    /// <summary>Disolución por luma: un degradado (de fábrica o una imagen gris) decide qué parte cambia primero.</summary>
    public const string Luma = "rodaje.luma";

    /// <summary>Empuje que se pasa y vuelve, como un rebote.</summary>
    public const string Bounce = "rodaje.bounce";

    /// <summary>Fundido de audio de potencia constante (seno y coseno): la suma de potencias vale 1.</summary>
    public const string AudioPower = "rodaje.audio-power";

    /// <summary>Fundido de audio de ganancia constante (lineal): la suma de ganancias vale 1.</summary>
    public const string AudioGain = "rodaje.audio-gain";

    /// <summary>Fundido de audio exponencial: lineal en decibelios, de −60 dB a 0.</summary>
    public const string AudioExponential = "rodaje.audio-exponential";

    /// <summary>Si <paramref name="type"/> es una de las transiciones de audio.</summary>
    public static bool IsAudio(string? type) => type is AudioPower or AudioGain or AudioExponential;

    /// <summary>Parámetros que comparten varias transiciones.</summary>
    public static class CommonParameters
    {
        /// <summary>Dirección en grados; 0 avanza de izquierda a derecha y 90 de arriba abajo.</summary>
        public const string Angle = "angle";

        /// <summary>Ancho del borde suave, de 0 a 1 del cuadro.</summary>
        public const string Softness = "softness";

        /// <summary>Ancho del borde de color, de 0 a 0,2 del cuadro (0 sin borde).</summary>
        public const string BorderWidth = "borderWidth";

        public const string BorderRed = "borderRed";

        public const string BorderGreen = "borderGreen";

        public const string BorderBlue = "borderBlue";

        /// <summary>Desenfoque de movimiento: fracción del cuadro que el obturador está abierto (0 apagado, 0,5 es 180°).</summary>
        public const string MotionBlur = "motionBlur";

        /// <summary>Color de la luz (destello, barrido con luz, quemado).</summary>
        public const string Red = "red";

        public const string Green = "green";

        public const string Blue = "blue";

        public const string Intensity = "intensity";

        /// <summary>Semilla del ruido: otra semilla da otras manchas o franjas.</summary>
        public const string Seed = "seed";
    }

    public static class SlideParameters
    {
        /// <summary>Cuánto acompaña el clip que sale al que entra (0 quieto, 1 lo empuja).</summary>
        public const string Parallax = "parallax";
    }

    public static class ZoomBlurParameters
    {
        /// <summary>Fuerza del acercamiento y del desenfoque radial.</summary>
        public const string Strength = "strength";

        public const string CenterX = "centerX";

        public const string CenterY = "centerY";
    }

    public static class SpinParameters
    {
        /// <summary>Grados que gira en toda la transición (negativo, al revés).</summary>
        public const string Angle = "angle";

        /// <summary>Acercamiento a mitad del giro.</summary>
        public const string Zoom = "zoom";
    }

    public static class LightWipeParameters
    {
        /// <summary>Ancho de la franja de luz, de 0 a 0,5 del cuadro.</summary>
        public const string Width = "width";
    }

    public static class GlitchParameters
    {
        public const string Amount = "amount";

        /// <summary>Cantidad de franjas horizontales.</summary>
        public const string Bands = "bands";

        /// <summary>Separación de los canales, en fracción del ancho.</summary>
        public const string Split = "split";
    }

    public static class FilmBurnParameters
    {
        /// <summary>Tamaño de las manchas, en fracción del alto.</summary>
        public const string Scale = "scale";
    }

    public static class FlashParameters
    {
        /// <summary>Cuánto dura el destello, en fracción de la transición.</summary>
        public const string Width = "width";
    }

    public static class LumaOptions
    {
        /// <summary>Degradado de fábrica: <see cref="Effects.EffectRegistry.LumaGradients"/>.</summary>
        public const string Gradient = "gradient";

        /// <summary><c>file:</c> y la ruta de una imagen gris (PNG, JPEG…): manda sobre el degradado de fábrica.</summary>
        public const string Image = "image";

        /// <summary><c>off</c> u <c>on</c>: cambia primero lo claro en lugar de lo oscuro.</summary>
        public const string Invert = "invert";
    }

    public static class BounceParameters
    {
        /// <summary>Cuánto se pasa antes de volver, de 0 (empuje normal) a 1.</summary>
        public const string Bounce = "bounce";
    }

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
