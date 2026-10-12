using Rodaje.Media.Ai;
using Rodaje.Media.Gpu;
using Rodaje.Media.Tests.Gpu;

namespace Rodaje.Media.Tests.Ai;

/// <summary>El perfil de IA con equipos simulados: de sin GPU o integrada con 8 GB de RAM a 24 GB de VRAM y 32 hilos.</summary>
public sealed class AiHardwarePlanTests
{
    private static AiMachine Discrete(int vramMb, int ramMb = 16_384, int cores = 16, int? freeMb = null) =>
        new(true, false, GpuVendor.Nvidia, "GPU de prueba", vramMb, ramMb / 2, ramMb, freeMb ?? ramMb / 2, cores);

    private static AiMachine NoGpu(int ramMb = 16_384, int cores = 16, int? freeMb = null) => AiMachine.CpuOnly(ramMb, freeMb ?? ramMb / 2, cores);

    private static AiMachine Integrated(int ramMb, int cores = 8) =>
        new(true, true, GpuVendor.Intel, "Intel(R) Iris(R) Xe Graphics", 128, ramMb / 2, ramMb, ramMb / 2, cores);

    [Theory]
    // VRAM, presupuesto, Whisper, en GPU, tramos a la vez, capas del traductor, lado de MODNet, alta en GPU, pesados a la vez.
    [InlineData(0, 0, WhisperModel.Small, false, 2, 0, 512, false, 1)]
    [InlineData(2048, 1280, WhisperModel.Small, true, 2, 10, 512, false, 1)]
    [InlineData(4096, 2560, WhisperModel.Turbo, true, 2, 25, 512, true, 1)]
    [InlineData(8192, 5120, WhisperModel.Turbo, true, 2, AiHardwarePlan.AllLayers, 512, true, 1)]
    [InlineData(12_288, 9216, WhisperModel.Turbo, true, 3, AiHardwarePlan.AllLayers, 512, true, 2)]
    [InlineData(24_576, 21_504, WhisperModel.Turbo, true, 4, AiHardwarePlan.AllLayers, 512, true, 3)]
    public void Decide_por_VRAM(int vram, int budget, WhisperModel whisper, bool whisperOnGpu, int parallel, int layers, int side, bool high, int heavy)
    {
        AiPlan plan = AiHardwarePlan.For(vram == 0 ? NoGpu() : Discrete(vram));
        Assert.Equal(budget, plan.GpuBudgetMb);
        Assert.Equal(whisper, plan.Subtitles.Model);
        Assert.Equal(whisperOnGpu ? AiDevice.Gpu : AiDevice.Cpu, plan.Subtitles.Device);
        Assert.Equal(parallel, plan.Subtitles.Parallel);
        Assert.Equal(layers, plan.Translate.GpuLayers);
        Assert.Equal(layers > 0 ? AiDevice.Gpu : AiDevice.Cpu, plan.Translate.Device);
        Assert.Equal(side, plan.Vision.MaskSide);
        Assert.Equal(high, plan.Vision.HighOnGpu);
        Assert.Equal(heavy, plan.MaxHeavyAtOnce);
        Assert.Equal(vram == 0 ? AiDevice.Cpu : AiDevice.Gpu, plan.Vision.Device);
        // Lo reservado nunca pasa del presupuesto cuando el módulo va por la GPU.
        Assert.True(plan.Subtitles.VramMb <= Math.Max(budget, 0) || plan.Subtitles.Device == AiDevice.Cpu);
        Assert.True(plan.Translate.VramMb <= budget);
    }

    [Fact]
    public void Descarga_parcial_calculada_no_todo_o_nada()
    {
        int previous = -1;
        foreach (int vram in new[] { 1536, 2048, 2560, 3072, 3584, 4096, 4608, 5120, 5632 })
        {
            TranslatePlan t = AiHardwarePlan.For(Discrete(vram)).Translate;
            Assert.True(t.GpuLayers >= previous, $"{vram} MB: {t.GpuLayers} capas");
            Assert.True(t.GpuLayers == 0 || t.GpuLayers >= AiHardwarePlan.TranslateMinLayers);
            Assert.True(t.GpuLayers is <= AiHardwarePlan.TranslateLayers or AiHardwarePlan.AllLayers);
            previous = t.GpuLayers;
        }

        TranslatePlan four = AiHardwarePlan.For(Discrete(4096)).Translate;
        Assert.InRange(four.GpuFraction, 0.6, 0.8);
        // La RAM que falta es la de las capas que quedan en la CPU.
        Assert.True(four.RamMb < AiHardwarePlan.For(NoGpu()).Translate.RamMb);
    }

    [Fact]
    public void Integrada_usa_la_compartida_con_tope()
    {
        AiPlan eight = AiHardwarePlan.For(Integrated(8192));
        Assert.Equal(128 + 1024, eight.GpuBudgetMb);
        Assert.Equal(AiDevice.Gpu, eight.Subtitles.Device);
        Assert.Equal(WhisperModel.Small, eight.Subtitles.Model);
        Assert.Equal(1, eight.Subtitles.Parallel);
        Assert.Equal(AiDevice.Cpu, eight.Translate.Device);
        Assert.Equal(AiDevice.Gpu, eight.Vision.Device);
        Assert.False(eight.Vision.HighOnGpu);
        Assert.Equal(1, eight.CpuSlots);
        Assert.False(eight.LightModulesTogether);

        Assert.Equal(2048, AiHardwarePlan.For(Integrated(16_384)).GpuBudgetMb);
        Assert.Equal(2048, AiHardwarePlan.For(Integrated(32_768)).GpuBudgetMb);
        Assert.Equal(1536, AiHardwarePlan.For(Integrated(32_768), AiQuality.Fast).GpuBudgetMb);
        Assert.Equal(3072, AiHardwarePlan.For(Integrated(32_768), AiQuality.Max).GpuBudgetMb);
    }

    [Theory]
    // Hilos, RAM: hilos de cómputo, hilos y tramos de Whisper en la CPU, trabajos de CPU a la vez, cuadros de visión a la vez.
    [InlineData(4, 8192, 4, 4, 1, 1, 1)]
    [InlineData(8, 16_384, 4, 4, 1, 2, 1)]
    [InlineData(16, 16_384, 8, 4, 2, 3, 2)]
    [InlineData(32, 32_768, 16, 8, 2, 3, 2)]
    public void Decide_por_hilos_sin_GPU(int cores, int ram, int threads, int whisperThreads, int whisperParallel, int cpuSlots, int visionParallel)
    {
        AiPlan plan = AiHardwarePlan.For(NoGpu(ram, cores));
        Assert.Equal(threads, plan.CpuThreads);
        Assert.Equal(whisperThreads, plan.Subtitles.Threads);
        Assert.Equal(whisperParallel, plan.Subtitles.Parallel);
        Assert.Equal(cpuSlots, plan.CpuSlots);
        Assert.Equal(visionParallel, plan.Vision.Parallel);
        Assert.Equal(threads, plan.Translate.Threads);
        Assert.Equal(cores <= 4 ? 384 : 512, plan.Vision.MaskSide);
        Assert.Equal(ram <= 8192 ? 4 : 8, plan.Vision.FramesPerCall);
    }

    [Fact]
    public void Calidad_rapida_y_maxima()
    {
        AiPlan fast = AiHardwarePlan.For(Discrete(4096), AiQuality.Fast);
        Assert.Equal(WhisperModel.Small, fast.Subtitles.Model);
        Assert.Equal(384, fast.Vision.MaskSide);

        Assert.Equal(640, AiHardwarePlan.For(Discrete(8192), AiQuality.Max).Vision.MaskSide);
        Assert.Equal(512, AiHardwarePlan.For(Discrete(4096), AiQuality.Max).Vision.MaskSide);
        Assert.Equal(WhisperModel.Base, AiHardwarePlan.For(NoGpu(), AiQuality.Fast).Subtitles.Model);
        Assert.Equal(WhisperModel.Turbo, AiHardwarePlan.For(NoGpu(32_768, 16), AiQuality.Max).Subtitles.Model);
        Assert.Equal(WhisperModel.Small, AiHardwarePlan.For(NoGpu(16_384, 8), AiQuality.Max).Subtitles.Model);
    }

    [Fact]
    public void Poca_RAM_libre_baja_a_Whisper_base()
    {
        AiPlan plan = AiHardwarePlan.For(NoGpu(8192, 4, freeMb: 700));
        Assert.Equal(WhisperModel.Base, plan.Subtitles.Model);
        AiEstimate translate = AiEstimator.ForTranslate(plan, 4000);
        Assert.NotNull(translate.Warning);
        Assert.NotNull(translate.Alternative);
    }

    [Fact]
    public void En_la_CPU_si_la_GPU_falla()
    {
        AiPlan cpu = AiHardwarePlan.For(Discrete(4096)).OnCpu();
        Assert.Equal(0, cpu.GpuBudgetMb);
        Assert.Equal(AiDevice.Cpu, cpu.Subtitles.Device);
        Assert.Equal(0, cpu.Translate.GpuLayers);
        Assert.Equal(AiDevice.Cpu, cpu.Vision.Device);
        Assert.Equal(0, cpu.Subtitles.VramMb + cpu.Translate.VramMb + cpu.Vision.VramMb);
    }

    [Fact]
    public void Simulacion_de_VRAM_y_RAM()
    {
        var profile = new GpuProfile([GpuProfileTests.Rtx3050], HardwareDecodeProfiles.None, 16);
        AiMachine real = AiMachine.From(profile, 16L << 30, 8L << 30);
        Assert.Equal(4096, real.DedicatedVramMb);
        Assert.False(real.Integrated);

        AiMachine two = AiMachine.From(profile, 16L << 30, 8L << 30, simulatedVramMb: 2048, simulatedRamMb: 8192);
        Assert.Equal(2048, two.DedicatedVramMb);
        Assert.Equal(8192, two.RamTotalMb);
        Assert.Equal(4096, two.RamFreeMb);

        AiMachine none = AiMachine.From(profile, 16L << 30, 8L << 30, simulatedVramMb: 0);
        Assert.False(none.HasGpu);
        Assert.False(AiMachine.From(GpuProfile.CpuOnly(8), 8L << 30, 4L << 30).HasGpu);
    }

    [Fact]
    public void Reconoce_integradas()
    {
        Assert.True(AiMachine.IsIntegrated(GpuProfileTests.IrisXe));
        Assert.False(AiMachine.IsIntegrated(GpuProfileTests.ArcA770));
        Assert.False(AiMachine.IsIntegrated(GpuProfileTests.Rtx3050));
        Assert.False(AiMachine.IsIntegrated(GpuProfileTests.Rx7800));
        Assert.True(AiMachine.IsIntegrated(new GpuAdapter("AMD Radeon 780M Graphics", 0x1002, 0x15BF, 2L << 30, 16L << 30, false, null)));
        Assert.True(AiMachine.IsIntegrated(new GpuAdapter("AMD Radeon(TM) Graphics", 0x1002, 0x1638, 512L << 20, 8L << 30, false, null)));
    }

    [Fact]
    public void Estimaciones_escalan_y_aprenden_de_lo_medido()
    {
        AiPlan four = AiHardwarePlan.For(Discrete(4096));
        AiPlan cpu = AiHardwarePlan.For(NoGpu());
        AiEstimate gpuSubs = AiEstimator.ForSubtitles(four, 600);
        AiEstimate cpuSubs = AiEstimator.ForSubtitles(cpu, 600);
        Assert.True(gpuSubs.Seconds < cpuSubs.Seconds);
        Assert.Contains("large-v3-turbo", gpuSubs.Where, StringComparison.Ordinal);
        Assert.InRange(cpuSubs.Seconds, 60, 240);

        AiEstimate high = AiEstimator.ForRemoveBackground(AiHardwarePlan.For(Discrete(2048)), 300, high: true);
        Assert.NotNull(high.Warning);
        Assert.Equal("Usar la calidad normal (MODNet)", high.Alternative);
        Assert.Null(AiEstimator.ForRemoveBackground(four, 300, high: true).Warning);

        AiEstimate full = AiEstimator.ForTranslate(AiHardwarePlan.For(Discrete(8192)), 4000);
        AiEstimate partial = AiEstimator.ForTranslate(four, 4000);
        AiEstimate none = AiEstimator.ForTranslate(cpu, 4000);
        Assert.True(full.Seconds < partial.Seconds && partial.Seconds < none.Seconds);

        var rates = new AiMeasuredRates("equipo");
        rates.Record(gpuSubs.Key, 600, 20);
        AiEstimate measured = AiEstimator.ForSubtitles(four, 600, rates);
        Assert.True(measured.Measured);
        Assert.Equal(20, measured.Seconds, 3);
    }

    [Fact]
    public void Lo_medido_se_guarda_y_se_olvida_si_cambia_el_equipo()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ia-tiempos-{Guid.NewGuid():N}.json");
        try
        {
            new AiMeasuredRates("A", path).Record("subtitulos|small|cpu", 120, 30);
            Assert.Equal(4, new AiMeasuredRates("A", path).Rate("subtitulos|small|cpu"));
            Assert.Null(new AiMeasuredRates("B", path).Rate("subtitulos|small|cpu"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3, "unos segundos")]
    [InlineData(42, "unos 40 s")]
    [InlineData(200, "unos 3 min")]
    [InlineData(3600, "unas 1 h")]
    [InlineData(4800, "unas 1 h 20 min")]
    public void Formato_del_tiempo(double seconds, string text) => Assert.Equal(text, AiEstimator.Format(seconds));
}
