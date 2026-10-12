using System.Globalization;
using Rodaje.Media.Ai;
using Rodaje.Media.Gpu;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// El perfil de IA de este equipo para la app: arma el <see cref="AiMachine"/> (la GPU una vez, la RAM libre en cada
/// consulta), el <see cref="AiPlan"/> con la calidad elegida en Ver › Calidad de la IA y las velocidades medidas.
/// </summary>
public sealed class AiProfile
{
    private readonly Func<GpuProfile> _gpu;
    private readonly Func<(long Total, long Available)> _memory;
    private readonly Func<AiQuality> _quality;
    private readonly int? _simulatedVram;
    private readonly int? _simulatedRam;

    public AiProfile(Func<GpuProfile> gpu, Func<(long Total, long Available)> memory, Func<AiQuality> quality, AiMeasuredRates rates,
        int? simulatedVramMb = null, int? simulatedRamMb = null)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _quality = quality ?? throw new ArgumentNullException(nameof(quality));
        Rates = rates ?? throw new ArgumentNullException(nameof(rates));
        _simulatedVram = simulatedVramMb;
        _simulatedRam = simulatedRamMb;
    }

    /// <summary>Lo medido en este equipo; cada función anota aquí lo que tardó.</summary>
    public AiMeasuredRates Rates { get; }

    public AiQuality Quality => _quality();

    public AiMachine Machine
    {
        get
        {
            (long total, long available) = _memory();
            return AiMachine.From(_gpu(), total, available, _simulatedVram, _simulatedRam);
        }
    }

    /// <summary>El plan con la RAM libre de ahora y la calidad elegida.</summary>
    public AiPlan Plan() => AiHardwarePlan.For(Machine, _quality());

    /// <summary>El de este equipo: <see cref="GpuProfileReader.Current"/>, la RAM de Windows y <c>ia-tiempos.json</c>.</summary>
    public static AiProfile ForThisMachine(Func<AiQuality> quality)
    {
        int? vram = AiMachine.ReadSimulation(AiMachine.SimulateVramVariable);
        int? ram = AiMachine.ReadSimulation(AiMachine.SimulateRamVariable);
        GpuProfile gpu = GpuProfileReader.Current;
        string key = string.Create(CultureInfo.InvariantCulture, $"{gpu.Key}|{gpu.LogicalCores}|{vram}|{ram}");
        return new AiProfile(() => GpuProfileReader.Current, SystemMemory.Read, quality,
            new AiMeasuredRates(key, Path.Combine(AppDataPaths.Root, "ia-tiempos.json")), vram, ram);
    }
}

/// <summary>Lo que pide un trabajo de módulo: VRAM (MB; se reserva mientras viva el proceso) y si ocupa la CPU.</summary>
public readonly record struct ModuleDemand(int VramMb, bool UsesCpu)
{
    public static ModuleDemand For(ModuleDescriptor module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new ModuleDemand(module.VramMb, module.VramMb == 0);
    }

    public static ModuleDemand Cpu => new(0, true);

    public static ModuleDemand Gpu(int vramMb) => new(vramMb, false);
}

/// <summary>Dónde están los modelos de un módulo: junto al exe, en el almacén compartido de complementos o, al desarrollar, en <c>.entorno\modelos</c>.</summary>
public static class ModuleModelFiles
{
    public static string? Find(ModuleDescriptor module, string fileName)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        List<string> folders =
        [
            Path.Combine(module.Directory, "modelos"),
            Path.Combine(Path.GetDirectoryName(module.EntryPath) ?? module.Directory, "modelos"),
            Path.Combine(AppDataPaths.Root, "complementos", "modelos"),
        ];
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } modelos)
        {
            folders.Add(modelos);
        }

        if (Rodaje.Media.EnvironmentVariables.Read(Rodaje.Media.EnvironmentVariables.Entorno) is { } entorno)
        {
            folders.Add(Path.Combine(entorno, "modelos"));
        }

        for (DirectoryInfo? d = new(module.Directory); d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".entorno", "modelos")))
            {
                folders.Add(Path.Combine(d.FullName, ".entorno", "modelos"));
                break;
            }
        }

        return folders.Select(f => Path.Combine(f, fileName)).FirstOrDefault(File.Exists);
    }
}
