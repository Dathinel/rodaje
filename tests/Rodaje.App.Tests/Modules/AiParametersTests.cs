using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Translate;
using Rodaje.Media.Ai;
using Rodaje.Media.Gpu;

namespace Rodaje.App.Tests.Modules;

/// <summary>El plan de IA llega a cada módulo como parámetros opcionales, y el reparto de VRAM y de CPU lo sigue.</summary>
public sealed class AiParametersTests
{
    private static AiPlan Plan(int vramMb, int cores = 16, int ramMb = 16_384, AiQuality quality = AiQuality.Auto) =>
        AiHardwarePlan.For(vramMb == 0
            ? AiMachine.CpuOnly(ramMb, ramMb / 2, cores)
            : new AiMachine(true, false, GpuVendor.Nvidia, "GPU", vramMb, ramMb / 2, ramMb, ramMb / 2, cores), quality);

    private static string? Path(WhisperModel model) => $@"C:\m\{AiParameters.WhisperFile(model)}";

    [Fact]
    public void Subtitulos_en_GPU_llevan_el_modelo_de_la_CPU_de_repuesto()
    {
        using JsonDocument doc = AiParameters.Subtitles("es", Plan(4096), Path);
        JsonElement p = doc.RootElement;
        Assert.Equal("es", p.GetProperty("language").GetString());
        Assert.Equal("auto", p.GetProperty("device").GetString());
        Assert.EndsWith(AiParameters.TurboFile, p.GetProperty("model").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(AiParameters.SmallFile, p.GetProperty("cpuModel").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, p.GetProperty("parallel").GetInt32());

        using JsonDocument two = AiParameters.Subtitles("es", Plan(2048), Path);
        Assert.EndsWith(AiParameters.SmallFile, two.RootElement.GetProperty("model").GetString(), StringComparison.Ordinal);

        using JsonDocument big = AiParameters.Subtitles("auto", Plan(24_576), Path);
        Assert.Equal(4, big.RootElement.GetProperty("parallel").GetInt32());
    }

    [Fact]
    public void Subtitulos_en_CPU_y_variante_que_falta()
    {
        using JsonDocument doc = AiParameters.Subtitles("es", Plan(0, cores: 32), Path);
        JsonElement p = doc.RootElement;
        Assert.Equal("cpu", p.GetProperty("device").GetString());
        Assert.False(p.TryGetProperty("model", out _));
        Assert.EndsWith(AiParameters.SmallFile, p.GetProperty("cpuModel").GetString(), StringComparison.Ordinal);
        Assert.Equal(8, p.GetProperty("threads").GetInt32());
        Assert.Equal(2, p.GetProperty("parallel").GetInt32());

        AiPlan fast = Plan(0, quality: AiQuality.Fast);
        Assert.Equal(WhisperModel.Base, fast.Subtitles.Model);
        Assert.Equal(WhisperModel.Small, AiParameters.WithInstalledWhisper(fast, m => m != WhisperModel.Base).Subtitles.Model);
        AiPlan gpu = AiParameters.WithInstalledWhisper(Plan(4096), m => m == WhisperModel.Small);
        Assert.Equal(WhisperModel.Small, gpu.Subtitles.Model);
        Assert.Equal(AiHardwarePlan.SmallVramMb + AiHardwarePlan.WhisperParallelVramMb, gpu.Subtitles.VramMb);
    }

    [Fact]
    public void Traduccion_reparte_capas_y_se_suma_a_cada_llamada()
    {
        SubtitleText[] texts = [new("a", "Hola"), new("b", "Adiós")];
        JsonElement partial = Assert.Single(SubtitleTrackTranslation.Requests(texts, "es", "en", w => AiParameters.WriteTranslate(w, Plan(2048).Translate)));
        Assert.Equal("auto", partial.GetProperty("device").GetString());
        Assert.Equal(10, partial.GetProperty("gpuLayers").GetInt32());
        Assert.Equal(8, partial.GetProperty("threads").GetInt32());
        Assert.Equal(2, partial.GetProperty("segments").GetArrayLength());

        JsonElement cpu = Assert.Single(SubtitleTrackTranslation.Requests(texts, "es", "en", w => AiParameters.WriteTranslate(w, Plan(0).Translate)));
        Assert.Equal("cpu", cpu.GetProperty("device").GetString());
        Assert.Equal(0, cpu.GetProperty("gpuLayers").GetInt32());

        JsonElement full = Assert.Single(SubtitleTrackTranslation.Requests(texts, "es", "en", w => AiParameters.WriteTranslate(w, Plan(12_288).Translate)));
        Assert.Equal(AiHardwarePlan.AllLayers, full.GetProperty("gpuLayers").GetInt32());

        JsonElement v1 = Assert.Single(SubtitleTrackTranslation.Requests(texts, "es", "en"));
        Assert.False(v1.TryGetProperty("device", out _));
    }

    [Fact]
    public void Quitar_fondo_alta_va_a_la_CPU_si_no_cabe()
    {
        using JsonDocument high = AiParameters.RemoveBackground(Plan(2048), high: true, "s", reset: true);
        Assert.Equal("cpu", high.RootElement.GetProperty("dispositivo").GetString());
        Assert.Equal("alta", high.RootElement.GetProperty("calidad").GetString());
        Assert.True(high.RootElement.GetProperty("reiniciar").GetBoolean());

        using JsonDocument normal = AiParameters.RemoveBackground(Plan(2048), high: false, "s", reset: false);
        Assert.Equal("gpu", normal.RootElement.GetProperty("dispositivo").GetString());
        Assert.Equal(512, normal.RootElement.GetProperty("lado").GetInt32());

        using JsonDocument four = AiParameters.RemoveBackground(Plan(4096), high: true, "s", reset: false);
        Assert.Equal("gpu", four.RootElement.GetProperty("dispositivo").GetString());

        using JsonDocument cpu = AiParameters.Detect(Plan(0, cores: 4, ramMb: 8192), faces: true);
        Assert.Equal("cpu", cpu.RootElement.GetProperty("dispositivo").GetString());
        Assert.Equal("rostros", cpu.RootElement.GetProperty("objetivo").GetString());

        Assert.Equal(ModuleDemand.Cpu, AiParameters.Demand(AiDevice.Cpu, 600));
        Assert.Equal(ModuleDemand.Gpu(600), AiParameters.Demand(AiDevice.Gpu, 600));
    }

    [Fact]
    public void Presupuesto_con_mucha_VRAM_admite_dos_pesados_y_la_reserva_crece()
    {
        AiPlan big = Plan(12_288);
        var budget = new VramBudget(big.GpuBudgetMb, big.MaxHeavyAtOnce);
        Assert.Equal(9216, budget.BudgetMb);
        Assert.True(budget.TryReserve("subtitulos", 2200));
        Assert.True(budget.TryReserve("traducir", 3300));
        Assert.False(budget.TryReserve("vision", 2400));
        Assert.Equal(["traducir"], budget.Blocking(2400));

        var four = new VramBudget(Plan(4096).GpuBudgetMb, 1);
        Assert.True(four.TryReserve("vision", 600));
        Assert.True(four.TryReserve("vision", 2400));
        Assert.Equal(2400, four.UsedMb);
        Assert.True(four.TryReserve("vision", 600));
        Assert.Equal(2400, four.UsedMb);
        Assert.False(four.TryReserve("subtitulos", 1500));
    }

    [Fact]
    public void El_perfil_sigue_la_calidad_elegida_y_la_simulacion()
    {
        var quality = AiQuality.Auto;
        var gpu = new GpuProfile([new GpuAdapter("NVIDIA GeForce RTX 3050 Laptop GPU", 0x10DE, 0x25A2, 4L << 30, 8L << 30, false, "1")],
            HardwareDecodeProfiles.None, 16);
        var profile = new AiProfile(() => gpu, () => (16L << 30, 8L << 30), () => quality, new AiMeasuredRates("x"), simulatedVramMb: 2048);
        Assert.Equal(1280, profile.Plan().GpuBudgetMb);
        Assert.Equal(WhisperModel.Small, profile.Plan().Subtitles.Model);
        quality = AiQuality.Fast;
        Assert.Equal(384, profile.Plan().Vision.MaskSide);
    }
}
