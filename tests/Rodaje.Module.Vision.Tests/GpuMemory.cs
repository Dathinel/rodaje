using System.Diagnostics;
using System.Globalization;

namespace Rodaje.Module.Vision.Tests;

/// <summary>
/// Memoria de GPU de un proceso, de los contadores de Windows «GPU Process Memory» (nvidia-smi no la da por proceso con
/// WDDM): dedicada (VRAM) y compartida (RAM del sistema que la GPU usa cuando no le alcanza). DirectML no devuelve lo que
/// reservó, así que leerla después de correr el modelo da el pico.
/// </summary>
internal static class GpuMemory
{
    public static (double Dedicated, double Shared)? Mb(int processId)
    {
        try
        {
            using var shell = Process.Start(new ProcessStartInfo("powershell.exe",
                ["-NoProfile", "-Command", $"$s = (Get-Counter '\\GPU Process Memory(pid_{processId}_*)\\Dedicated Usage','\\GPU Process Memory(pid_{processId}_*)\\Shared Usage').CounterSamples; " +
                    "'{0} {1}' -f [long](($s | ? Path -like '*dedicated*' | Measure-Object CookedValue -Sum).Sum), [long](($s | ? Path -like '*shared*' | Measure-Object CookedValue -Sum).Sum)"])
            { RedirectStandardOutput = true, CreateNoWindow = true })!;
            string[] parts = shell.StandardOutput.ReadToEnd().Trim().Split(' ');
            shell.WaitForExit();
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out long dedicated) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out long shared)
                ? (dedicated / 1048576.0, shared / 1048576.0)
                : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
