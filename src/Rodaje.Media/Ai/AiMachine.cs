using System.Globalization;
using System.Text.RegularExpressions;
using Rodaje.Media.Gpu;

namespace Rodaje.Media.Ai;

/// <summary>Cuánto se le pide a la IA: lo que decide el perfil, lo más rápido que sirva o lo mejor que quepa.</summary>
public enum AiQuality
{
    Auto,
    Fast,
    Max,
}

/// <summary>
/// Lo que importa del equipo para la IA local: la GPU principal (VRAM dedicada y compartida, fabricante, si es integrada),
/// la RAM total y libre y los hilos de la CPU. Lo arma <see cref="From"/> con el <see cref="GpuProfile"/> y la memoria del
/// sistema; las pruebas lo construyen a mano.
/// </summary>
/// <param name="HasGpu">Hay una GPU por hardware que pueda correr modelos (Vulkan o DirectML).</param>
/// <param name="Integrated">La GPU comparte la RAM del sistema (Intel UHD/Iris, Radeon de un APU).</param>
/// <param name="Vendor">Fabricante de la GPU.</param>
/// <param name="GpuName">Nombre de la GPU.</param>
/// <param name="DedicatedVramMb">VRAM propia en MB (en una integrada, la reserva de la BIOS).</param>
/// <param name="SharedVramMb">Memoria del sistema que puede usar la GPU, en MB.</param>
/// <param name="RamTotalMb">RAM física total.</param>
/// <param name="RamFreeMb">RAM disponible ahora.</param>
/// <param name="LogicalCores">Hilos lógicos de la CPU.</param>
public sealed record AiMachine(bool HasGpu, bool Integrated, GpuVendor Vendor, string GpuName, int DedicatedVramMb, int SharedVramMb,
    int RamTotalMb, int RamFreeMb, int LogicalCores)
{
    /// <summary>Simula otra VRAM dedicada (MB; 0 = sin GPU). Solo para pruebas.</summary>
    public const string SimulateVramVariable = "RODAJE_SIMULAR_VRAM_MB";

    /// <summary>Simula otra RAM total (MB); la libre se escala en la misma proporción. Solo para pruebas.</summary>
    public const string SimulateRamVariable = "RODAJE_SIMULAR_RAM_MB";

    private static readonly Regex IntelDiscrete = new(@"Arc.*\b[AB]\d{3}M?\b|Arc\(TM\) Pro A", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AmdApu = new(@"Radeon\(TM\)\s*(\d{3}M\s*)?Graphics|Radeon\s*\d{3}M\b|Radeon\s*Vega\s*\d+\s*Graphics|Radeon\(TM\)\s*Vega",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>El equipo sin GPU por hardware.</summary>
    public static AiMachine CpuOnly(int ramTotalMb, int ramFreeMb, int logicalCores) =>
        new(false, false, GpuVendor.Unknown, "", 0, 0, ramTotalMb, ramFreeMb, logicalCores);

    /// <summary>
    /// El perfil para la IA desde el de la GPU y la memoria. <paramref name="simulatedVramMb"/> y <paramref name="simulatedRamMb"/>
    /// (de <see cref="SimulateVramVariable"/> y <see cref="SimulateRamVariable"/>) reemplazan la VRAM y la RAM reales.
    /// </summary>
    public static AiMachine From(GpuProfile profile, long ramTotalBytes, long ramFreeBytes, int? simulatedVramMb = null, int? simulatedRamMb = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int ramTotal = ToMb(ramTotalBytes);
        int ramFree = Math.Min(ToMb(ramFreeBytes), ramTotal);
        if (simulatedRamMb is > 0 and { } ram && ramTotal > 0)
        {
            ramFree = (int)((long)ramFree * ram / ramTotal);
            ramTotal = ram;
        }

        int cores = Math.Max(1, profile.LogicalCores);
        if (profile.Primary is not { } gpu || simulatedVramMb == 0)
        {
            return CpuOnly(ramTotal, ramFree, cores);
        }

        int dedicated = ToMb(gpu.DedicatedVideoMemory);
        int shared = ToMb(gpu.SharedSystemMemory);
        bool integrated = IsIntegrated(gpu);
        if (simulatedVramMb is > 0 and { } vram)
        {
            // Se simula una tarjeta dedicada de ese tamaño.
            dedicated = vram;
            integrated = false;
        }

        return new AiMachine(true, integrated, gpu.Vendor, gpu.Name, dedicated, shared, ramTotal, ramFree, cores);
    }

    /// <summary>Lee <see cref="SimulateVramVariable"/> o <see cref="SimulateRamVariable"/>: un entero ≥ 0, o <c>null</c>.</summary>
    public static int? ReadSimulation(string variable) =>
        EnvironmentVariables.Read(variable) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int mb) ? mb : null;

    /// <summary>Integrada: poca VRAM propia, o un nombre de integrada de Intel o de APU de AMD.</summary>
    public static bool IsIntegrated(GpuAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter.DedicatedVideoMemory < (512L << 20)
            || (adapter.Vendor == GpuVendor.Intel && !IntelDiscrete.IsMatch(adapter.Name))
            || (adapter.Vendor == GpuVendor.Amd && AmdApu.IsMatch(adapter.Name));
    }

    /// <summary>Para el diagnóstico: «RTX 3050 (4 GB de VRAM), 16 GB de RAM (9 libres), 16 hilos».</summary>
    public string Describe()
    {
        string gpu = !HasGpu ? "sin GPU"
            : Integrated ? string.Create(CultureInfo.InvariantCulture, $"{GpuName} (integrada, {Gb(SharedVramMb)} compartidos)")
            : string.Create(CultureInfo.InvariantCulture, $"{GpuName} ({Gb(DedicatedVramMb)} de VRAM)");
        return string.Create(CultureInfo.InvariantCulture, $"{gpu}, {Gb(RamTotalMb)} de RAM ({Gb(RamFreeMb)} libres), {LogicalCores} hilos");
    }

    internal static string Gb(int mb) => mb >= 1024
        ? (mb / 1024.0).ToString(mb % 1024 == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo("es")) + " GB"
        : mb.ToString(CultureInfo.InvariantCulture) + " MB";

    private static int ToMb(long bytes) => (int)Math.Clamp(bytes / (1024 * 1024), 0, int.MaxValue);
}
