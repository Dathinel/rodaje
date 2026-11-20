using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render.Effects;

namespace Rodaje.App.Services.RenderCache;

/// <summary>Constantes de calibración del modelo de coste de render, medidas en F0 (<c>docs/render-en-cache.md</c>).</summary>
public static class RenderCostCalibration
{
    /// <summary>Abrir un decodificador y dar su primer cuadro en 1080p, en ms (unos 180 en 4K).</summary>
    public const double DecoderOpenMs = 45.0;

    /// <summary>Cuánto más cuesta componer en WARP que en la GPU (38,6 frente a 0,7 ms en la escena C).</summary>
    public const double NoGpuFactor = 54.0;

    /// <summary>Coste base de composición por capa en milisegundos.</summary>
    public const double BaseCompositionMsPerLayer = 0.05;

    /// <summary>Proporción del periodo del cuadro que se da como presupuesto.</summary>
    public const double BudgetRatio = 0.8;
}

/// <summary>Clasificación del coste previsto de render respecto al presupuesto del cuadro.</summary>
public enum RenderCostClass
{
    /// <summary>Coste menor al 50 % del presupuesto (franja ligera / sin franja).</summary>
    Light,

    /// <summary>Coste entre el 50 % y el 100 % del presupuesto (franja amarilla, con poco margen).</summary>
    Tight,

    /// <summary>Coste superior al 100 % del presupuesto o con refrescos tarde medidos (franja roja).</summary>
    Heavy,
}

/// <summary>Parámetros de coste de una capa para estimar el coste de render de un cuadro.</summary>
/// <param name="DecodeMs">Milisegundos para decodificar un cuadro (0 para títulos o gráficos).</param>
/// <param name="OpensDecoder">Indica si en este cuadro se abre un decodificador nuevo.</param>
/// <param name="EffectsMs">Milisegundos de GPU de la pila de efectos de la capa.</param>
/// <param name="IntermediateCount">Cantidad de intermedios que necesita la capa (compuestos anidados, pasadas de desenfoque, etc.).</param>
/// <param name="IntermediateMs">Milisegundos por intermedio, escalado según la resolución.</param>
public readonly record struct LayerCostInput(
    double DecodeMs = 0,
    bool OpensDecoder = false,
    double EffectsMs = 0,
    int IntermediateCount = 0,
    double IntermediateMs = 0)
{
    /// <summary>Calcula los milisegundos de decodificación a partir de los cuadros por segundo medidos por DecodeSpeedProbe.</summary>
    /// <param name="fps">Cuadros por segundo de decodificación.</param>
    public static double DecodeMsFromFps(double fps) => fps > 0 ? 1000.0 / fps : 0;

    /// <summary>Calcula el coste de un intermedio para el tamaño especificado, escalado desde 1080p.</summary>
    /// <param name="width">Ancho de la secuencia.</param>
    /// <param name="height">Alto de la secuencia.</param>
    public static double CalculateIntermediateMs(int width, int height) =>
        EffectCost.IntermediateMs1080 * Math.Max(1, (double)width * height) / (1920.0 * 1080.0);

    /// <summary>Crea una capa de video a partir de cuadros por segundo de decodificación y resolución.</summary>
    /// <param name="decodeFps">Cuadros por segundo de decodificación.</param>
    /// <param name="opensDecoder">Indica si en este cuadro se abre un decodificador.</param>
    /// <param name="effectsMs">Milisegundos de GPU de los efectos.</param>
    /// <param name="intermediateCount">Cantidad de intermedios requeridos.</param>
    /// <param name="width">Ancho de la secuencia en píxeles.</param>
    /// <param name="height">Alto de la secuencia en píxeles.</param>
    public static LayerCostInput Video(
        double decodeFps,
        bool opensDecoder = false,
        double effectsMs = 0,
        int intermediateCount = 0,
        int width = 1920,
        int height = 1080) =>
        new(
            DecodeMs: DecodeMsFromFps(decodeFps),
            OpensDecoder: opensDecoder,
            EffectsMs: effectsMs,
            IntermediateCount: intermediateCount,
            IntermediateMs: CalculateIntermediateMs(width, height));

    /// <summary>Crea una capa de título o gráfico (sin decodificación).</summary>
    /// <param name="effectsMs">Milisegundos de GPU de los efectos.</param>
    /// <param name="intermediateCount">Cantidad de intermedios requeridos.</param>
    /// <param name="width">Ancho de la secuencia en píxeles.</param>
    /// <param name="height">Alto de la secuencia en píxeles.</param>
    public static LayerCostInput Title(
        double effectsMs = 0,
        int intermediateCount = 0,
        int width = 1920,
        int height = 1080) =>
        new(
            DecodeMs: 0,
            OpensDecoder: false,
            EffectsMs: effectsMs,
            IntermediateCount: intermediateCount,
            IntermediateMs: CalculateIntermediateMs(width, height));
}

/// <summary>Resultado de la estimación del coste de render de un cuadro.</summary>
/// <param name="Milliseconds">Milisegundos previstos para renderizar el cuadro.</param>
/// <param name="Class">Clasificación del coste respecto al presupuesto.</param>
/// <param name="BudgetMilliseconds">Presupuesto del cuadro en milisegundos.</param>
public readonly record struct RenderCost(double Milliseconds, RenderCostClass Class, double BudgetMilliseconds = 0);

/// <summary>Tramo continuo de cuadros [Start, End) con la misma clase de coste de render.</summary>
/// <param name="Start">Índice del primer cuadro del tramo (inclusivo).</param>
/// <param name="End">Índice final del tramo (exclusivo, Start + Longitud).</param>
/// <param name="Class">Clasificación de coste del tramo.</param>
public readonly record struct RenderCostSpan(long Start, long End, RenderCostClass Class)
{
    /// <summary>Longitud del tramo en cuadros.</summary>
    public long Length => End - Start;

    /// <summary>Desconstruye el tramo en una tupla (Start, End, Class).</summary>
    /// <param name="start">Índice del primer cuadro (inclusivo).</param>
    /// <param name="end">Índice final del tramo (exclusivo).</param>
    /// <param name="cls">Clase de coste.</param>
    public void Deconstruct(out long start, out long end, out RenderCostClass cls)
    {
        start = Start;
        end = End;
        cls = Class;
    }

    /// <summary>Conversión implícita a tupla para facilitar su uso.</summary>
    /// <param name="span">Tramo a convertir.</param>
    public static implicit operator (long Start, long End, RenderCostClass Class)(RenderCostSpan span) =>
        (span.Start, span.End, span.Class);

    /// <summary>Conversión implícita desde tupla para facilitar su uso.</summary>
    /// <param name="tuple">Tupla de tramo.</param>
    public static implicit operator RenderCostSpan((long Start, long End, RenderCostClass Class) tuple) =>
        new(tuple.Start, tuple.End, tuple.Class);
}

/// <summary>
/// Modelo puro de estimación del coste de render por cuadro y agrupación en tramos para la franja de render en caché.
/// </summary>
public static class RenderCostModel
{
    /// <summary>Calcula el presupuesto en milisegundos para una tasa de cuadros dada (80 % del periodo).</summary>
    /// <param name="fps">Tasa de cuadros por segundo.</param>
    public static double CalculateBudget(double fps) =>
        fps > 0 ? (1000.0 / fps) * RenderCostCalibration.BudgetRatio : 0;

    /// <summary>Calcula el presupuesto en milisegundos para una tasa de cuadros dada (80 % del periodo).</summary>
    /// <param name="rate">Tasa de cuadros de la secuencia.</param>
    public static double CalculateBudget(Rational rate) =>
        CalculateBudget(rate.ToDouble());

    /// <summary>Calcula el coste previsto de renderizado de un conjunto de capas en milisegundos.</summary>
    /// <param name="layers">Capas visibles en el cuadro.</param>
    /// <param name="hasGpu">Indica si hay acelerador por hardware (GPU); si es falso, aplica el factor sin GPU.</param>
    public static double CalculateFrameCost(IReadOnlyList<LayerCostInput> layers, bool hasGpu = true)
    {
        ArgumentNullException.ThrowIfNull(layers);
        double total = 0;
        foreach (LayerCostInput layer in layers)
        {
            double decode = layer.DecodeMs + (layer.OpensDecoder ? RenderCostCalibration.DecoderOpenMs : 0);
            double gpu = RenderCostCalibration.BaseCompositionMsPerLayer + layer.EffectsMs + (layer.IntermediateCount * layer.IntermediateMs);
            if (!hasGpu)
            {
                gpu *= RenderCostCalibration.NoGpuFactor;
            }

            total += decode + gpu;
        }

        return total;
    }

    /// <summary>Clasifica un coste en ms respecto a un presupuesto en ms.</summary>
    /// <param name="costMs">Coste previsto en milisegundos.</param>
    /// <param name="budgetMs">Presupuesto del cuadro en milisegundos.</param>
    public static RenderCostClass Classify(double costMs, double budgetMs)
    {
        if (budgetMs <= 0)
        {
            return costMs > 0 ? RenderCostClass.Heavy : RenderCostClass.Light;
        }

        if (costMs < 0.5 * budgetMs)
        {
            return RenderCostClass.Light;
        }

        if (costMs <= budgetMs)
        {
            return RenderCostClass.Tight;
        }

        return RenderCostClass.Heavy;
    }

    /// <summary>Evalúa el coste de render de un cuadro y devuelve su coste y clase.</summary>
    /// <param name="layers">Capas visibles en el cuadro.</param>
    /// <param name="sequenceFps">Tasa de cuadros por segundo de la secuencia.</param>
    /// <param name="hasGpu">Indica si hay aceleración por hardware disponible.</param>
    public static RenderCost EvaluateFrame(IReadOnlyList<LayerCostInput> layers, double sequenceFps, bool hasGpu = true)
    {
        double costMs = CalculateFrameCost(layers, hasGpu);
        double budgetMs = CalculateBudget(sequenceFps);
        RenderCostClass cls = Classify(costMs, budgetMs);
        return new RenderCost(costMs, cls, budgetMs);
    }

    /// <summary>Evalúa el coste de render de un cuadro y devuelve su coste y clase.</summary>
    /// <param name="layers">Capas visibles en el cuadro.</param>
    /// <param name="sequenceRate">Tasa de cuadros de la secuencia.</param>
    /// <param name="hasGpu">Indica si hay aceleración por hardware disponible.</param>
    public static RenderCost EvaluateFrame(IReadOnlyList<LayerCostInput> layers, Rational sequenceRate, bool hasGpu = true) =>
        EvaluateFrame(layers, sequenceRate.ToDouble(), hasGpu);

    /// <summary>
    /// Suma los milisegundos estimados de una colección de efectos para una resolución dada usando <see cref="EffectCost.Estimate"/>.
    /// </summary>
    /// <param name="effects">Colección de instancias de efecto.</param>
    /// <param name="width">Ancho de la secuencia en píxeles.</param>
    /// <param name="height">Alto de la secuencia en píxeles.</param>
    public static double EstimateEffectsCost(IEnumerable<EffectInstance> effects, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(effects);
        double total = 0;
        foreach (EffectInstance effect in effects)
        {
            if (effect.Enabled)
            {
                total += EffectCost.Estimate(effect.Type, opt => effect.Options.GetValueOrDefault(opt), width, height).Milliseconds;
            }
        }

        return total;
    }

    /// <summary>
    /// Agrupa las clases de cuadros contiguos de la misma clase en tramos (Start, End, Class).
    /// Si un cuadro está en <paramref name="lateFrames"/>, pasa a considerarse Heavy («lo medido manda»).
    /// </summary>
    /// <param name="frameClasses">Clase de cada cuadro.</param>
    /// <param name="lateFrames">Cuadros en los que se midió un refresco tarde (opcional).</param>
    /// <param name="startFrame">Índice del primer cuadro en la secuencia.</param>
    public static IReadOnlyList<RenderCostSpan> GroupSpans(
        IReadOnlyList<RenderCostClass> frameClasses,
        IEnumerable<long>? lateFrames = null,
        long startFrame = 0)
    {
        ArgumentNullException.ThrowIfNull(frameClasses);
        if (frameClasses.Count == 0)
        {
            return [];
        }

        HashSet<long>? lateSet = lateFrames switch
        {
            null => null,
            HashSet<long> hs => hs,
            _ => [.. lateFrames],
        };

        var spans = new List<RenderCostSpan>();
        long currentStart = startFrame;
        RenderCostClass currentClass = (lateSet?.Contains(startFrame) == true)
            ? RenderCostClass.Heavy
            : frameClasses[0];

        for (int i = 1; i < frameClasses.Count; i++)
        {
            long frame = startFrame + i;
            RenderCostClass cls = (lateSet?.Contains(frame) == true)
                ? RenderCostClass.Heavy
                : frameClasses[i];

            if (cls != currentClass)
            {
                spans.Add(new RenderCostSpan(currentStart, frame, currentClass));
                currentStart = frame;
                currentClass = cls;
            }
        }

        spans.Add(new RenderCostSpan(currentStart, startFrame + frameClasses.Count, currentClass));
        return spans;
    }

    /// <summary>
    /// Agrupa los costes de cuadros contiguos en tramos (Start, End, Class).
    /// Si un cuadro está en <paramref name="lateFrames"/>, pasa a considerarse Heavy («lo medido manda»).
    /// </summary>
    /// <param name="frameCosts">Coste estimado de cada cuadro.</param>
    /// <param name="lateFrames">Cuadros en los que se midió un refresco tarde (opcional).</param>
    /// <param name="startFrame">Índice del primer cuadro en la secuencia.</param>
    public static IReadOnlyList<RenderCostSpan> GroupSpans(
        IReadOnlyList<RenderCost> frameCosts,
        IEnumerable<long>? lateFrames = null,
        long startFrame = 0)
    {
        ArgumentNullException.ThrowIfNull(frameCosts);
        if (frameCosts.Count == 0)
        {
            return [];
        }

        var classes = new RenderCostClass[frameCosts.Count];
        for (int i = 0; i < frameCosts.Count; i++)
        {
            classes[i] = frameCosts[i].Class;
        }

        return GroupSpans(classes, lateFrames, startFrame);
    }
}
