using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.RenderCache;

public sealed class RenderCostModelTests
{
    [Fact]
    public void Presupuesto_a_24_30_y_60_fps()
    {
        double budget24 = RenderCostModel.CalculateBudget(24.0);
        double budget30 = RenderCostModel.CalculateBudget(30.0);
        double budget60 = RenderCostModel.CalculateBudget(60.0);

        Assert.Equal((1000.0 / 24.0) * RenderCostCalibration.BudgetRatio, budget24, precision: 6);
        Assert.Equal((1000.0 / 30.0) * RenderCostCalibration.BudgetRatio, budget30, precision: 6);
        Assert.Equal((1000.0 / 60.0) * RenderCostCalibration.BudgetRatio, budget60, precision: 6);

        // Con Rational
        Assert.Equal(budget24, RenderCostModel.CalculateBudget(Rational.Fps24), precision: 6);
        Assert.Equal(budget30, RenderCostModel.CalculateBudget(Rational.Fps30), precision: 6);
        Assert.Equal(budget60, RenderCostModel.CalculateBudget(Rational.Fps60), precision: 6);
    }

    [Fact]
    public void Cuadro_ligero_con_coste_menor_al_50_por_ciento_del_presupuesto()
    {
        // A 30 fps, el presupuesto es 26.666 ms; 50 % es 13.333 ms.
        // Capa ligera con 5 ms de decodificación y sin efectos complejos.
        LayerCostInput layer = new(
            DecodeMs: 5.0,
            OpensDecoder: false,
            EffectsMs: 0.1,
            IntermediateCount: 0,
            IntermediateMs: 0.0);

        RenderCost cost = RenderCostModel.EvaluateFrame([layer], 30.0, hasGpu: true);

        Assert.True(cost.Milliseconds < 0.5 * cost.BudgetMilliseconds);
        Assert.Equal(RenderCostClass.Light, cost.Class);
    }

    [Fact]
    public void Cuadro_justo_entre_el_50_y_el_100_por_ciento_del_presupuesto()
    {
        // A 30 fps, el presupuesto es 26.666 ms; 50 % es 13.333 ms.
        // Capa con 18 ms de coste total (entre 13.333 ms y 26.666 ms).
        LayerCostInput layer = new(
            DecodeMs: 18.0,
            OpensDecoder: false,
            EffectsMs: 0.5,
            IntermediateCount: 0,
            IntermediateMs: 0.0);

        RenderCost cost = RenderCostModel.EvaluateFrame([layer], 30.0, hasGpu: true);

        Assert.True(cost.Milliseconds >= 0.5 * cost.BudgetMilliseconds);
        Assert.True(cost.Milliseconds <= cost.BudgetMilliseconds);
        Assert.Equal(RenderCostClass.Tight, cost.Class);
    }

    [Fact]
    public void Cuadro_pesado_con_coste_superior_al_presupuesto()
    {
        // A 30 fps, el presupuesto es 26.666 ms.
        // Dos capas que suman más de 30 ms.
        LayerCostInput layer1 = new(DecodeMs: 20.0);
        LayerCostInput layer2 = new(DecodeMs: 15.0);

        RenderCost cost = RenderCostModel.EvaluateFrame([layer1, layer2], 30.0, hasGpu: true);

        Assert.True(cost.Milliseconds > cost.BudgetMilliseconds);
        Assert.Equal(RenderCostClass.Heavy, cost.Class);
    }

    [Fact]
    public void Apertura_de_decodificador_vuelve_pesado_un_cuadro()
    {
        // A 30 fps (presupuesto ~26.67 ms), una capa con 15 ms de decodificación es Tight.
        LayerCostInput withoutOpen = new(DecodeMs: 15.0, OpensDecoder: false);
        RenderCost costWithout = RenderCostModel.EvaluateFrame([withoutOpen], 30.0, hasGpu: true);
        Assert.Equal(RenderCostClass.Tight, costWithout.Class);

        // Al abrir decodificador, se suma DecoderOpenMs y el total pasa del presupuesto (Heavy).
        LayerCostInput withOpen = new(DecodeMs: 15.0, OpensDecoder: true);
        RenderCost costWith = RenderCostModel.EvaluateFrame([withOpen], 30.0, hasGpu: true);

        Assert.Equal(costWithout.Milliseconds + RenderCostCalibration.DecoderOpenMs, costWith.Milliseconds, precision: 6);
        Assert.Equal(RenderCostClass.Heavy, costWith.Class);
    }

    [Fact]
    public void Factor_sin_gpu_multiplica_operaciones_de_gpu()
    {
        double decodeMs = 4.0;
        double effectsMs = 2.0;
        int intermediates = 2;
        double intermediateMs = LayerCostInput.CalculateIntermediateMs(1920, 1080);

        LayerCostInput layer = new(
            DecodeMs: decodeMs,
            OpensDecoder: false,
            EffectsMs: effectsMs,
            IntermediateCount: intermediates,
            IntermediateMs: intermediateMs);

        double costWithGpu = RenderCostModel.CalculateFrameCost([layer], hasGpu: true);
        double costWithoutGpu = RenderCostModel.CalculateFrameCost([layer], hasGpu: false);

        double baseGpu = RenderCostCalibration.BaseCompositionMsPerLayer + effectsMs + (intermediates * intermediateMs);
        double expectedWithGpu = decodeMs + baseGpu;
        double expectedWithoutGpu = decodeMs + (baseGpu * RenderCostCalibration.NoGpuFactor);

        Assert.Equal(expectedWithGpu, costWithGpu, precision: 6);
        Assert.Equal(expectedWithoutGpu, costWithoutGpu, precision: 6);

        // Sin GPU, el coste es significativamente mayor.
        Assert.True(costWithoutGpu > costWithGpu);
    }

    [Fact]
    public void Agrupacion_en_tramos_con_un_tarde_medido_vuelve_rojo_un_tramo_verde_claro()
    {
        // 10 cuadros ligeros consecutivos
        RenderCostClass[] classes =
        [
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Light,
        ];

        // Sin cuadros tarde, genera un único tramo continuo
        IReadOnlyList<RenderCostSpan> originalSpans = RenderCostModel.GroupSpans(classes, lateFrames: null, startFrame: 0);
        Assert.Single(originalSpans);
        Assert.Equal(new RenderCostSpan(0, 10, RenderCostClass.Light), originalSpans[0]);

        // Con un refresco tarde medido en el cuadro 4, ese cuadro pasa a Heavy ("lo medido manda"),
        // dividiendo el tramo verde en tres tramos: [0, 4) Light, [4, 5) Heavy, [5, 10) Light.
        long[] lateFrames = [4];
        IReadOnlyList<RenderCostSpan> spansWithLate = RenderCostModel.GroupSpans(classes, lateFrames, startFrame: 0);

        Assert.Equal(3, spansWithLate.Count);
        Assert.Equal(new RenderCostSpan(0, 4, RenderCostClass.Light), spansWithLate[0]);
        Assert.Equal(new RenderCostSpan(4, 5, RenderCostClass.Heavy), spansWithLate[1]);
        Assert.Equal(new RenderCostSpan(5, 10, RenderCostClass.Light), spansWithLate[2]);
    }

    [Fact]
    public void Agrupacion_con_multiples_clases_y_tardes_consecutivos()
    {
        RenderCostClass[] classes =
        [
            RenderCostClass.Light,
            RenderCostClass.Light,
            RenderCostClass.Tight,
            RenderCostClass.Tight,
            RenderCostClass.Light,
        ];

        // Cuadros 1 y 2 tienen tarde medido:
        // Cuadro 0: Light
        // Cuadro 1: Heavy (era Light, pero tarde)
        // Cuadro 2: Heavy (era Tight, pero tarde) -> se fusiona con cuadro 1
        // Cuadro 3: Tight
        // Cuadro 4: Light
        long[] lateFrames = [101, 102];
        IReadOnlyList<RenderCostSpan> spans = RenderCostModel.GroupSpans(classes, lateFrames, startFrame: 100);

        Assert.Equal(4, spans.Count);
        Assert.Equal(new RenderCostSpan(100, 101, RenderCostClass.Light), spans[0]);
        Assert.Equal(new RenderCostSpan(101, 103, RenderCostClass.Heavy), spans[1]);
        Assert.Equal(new RenderCostSpan(103, 104, RenderCostClass.Tight), spans[2]);
        Assert.Equal(new RenderCostSpan(104, 105, RenderCostClass.Light), spans[3]);
    }

    [Fact]
    public void Estimacion_de_pila_de_efectos_con_efectos_deshabilitados()
    {
        EffectInstance effect1 = new() { Type = BuiltInEffects.Color, Enabled = true };
        EffectInstance effect2 = new() { Type = BuiltInEffects.Sharpen, Enabled = false };
        EffectInstance effect3 = new() { Type = BuiltInEffects.Curves, Enabled = true };

        double cost = RenderCostModel.EstimateEffectsCost([effect1, effect2, effect3], 1920, 1080);

        // Color (0.01) + Curves (0.05) a 1080p = 0.06 ms; Sharpen está apagado.
        Assert.Equal(0.06, cost, precision: 4);
    }
}
