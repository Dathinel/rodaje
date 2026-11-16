using Rodaje.Experimentos.Eco;
using Rodaje.Modules.Protocol;

// Eco del protocolo de módulos por socket de dominio Unix y memoria compartida (fase 0e de D-066).
// El mismo ejecutable hace de editor (la batería, sin argumentos) y de módulo (--rodaje-pipe <ruta>);
// --intruso y --editor-intermedio son los papeles auxiliares que lanzan las comprobaciones.
// --solo-socket corre la parte que también sirve en Windows (socket Unix, saludo y ping).
int i = Array.IndexOf(args, "--rodaje-pipe");
if (i >= 0)
{
    if (i + 1 >= args.Length || !Limits.RegionName(Path.GetFileName(args[i + 1])))
    {
        await Console.Error.WriteLineAsync("Este programa es un módulo de Rodaje (Eco); lo abre el editor.");
        return 64;
    }
    if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
    {
        await Console.Error.WriteLineAsync("El modo módulo solo está en Linux y macOS.");
        return 2;
    }
    return await Modulo.RunAsync(args[i + 1]);
}
if (args.Contains("--solo-socket"))
    return await SoloSocket.RunAsync();
if ((i = Array.IndexOf(args, "--intruso")) >= 0 && i + 1 < args.Length)
    return await Auxiliar.IntrusoAsync(args[i + 1]);
if ((i = Array.IndexOf(args, "--editor-intermedio")) >= 0 && i + 1 < args.Length)
    return await Auxiliar.EditorIntermedioAsync(args[i + 1]);
if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
{
    await Console.Error.WriteLineAsync("La batería completa corre en Linux y macOS; aquí sirve «eco --solo-socket».");
    return 2;
}
return await Suite.RunAsync();

namespace Rodaje.Experimentos.Eco
{
    /// <summary>El ModuleInfo del módulo de eco: una función «eco» que copia un búfer de video en otro.</summary>
    internal static class EcoInfo
    {
        public static readonly ModuleInfo Modulo = Crear();

        private static ModuleInfo Crear()
        {
            ModuleInfo info = new("rodaje-eco-unix", "0.1.0", "Eco Unix",
                [new FunctionInfo("eco", "Copiar un búfer en otro", Cancellable: false, ReportsProgress: false, Inputs: ["video"], Outputs: ["video"])],
                Device: "cpu");
            if (info.Validate() is { } reason)
                throw new ArgumentException(reason);
            return info;
        }
    }
}
