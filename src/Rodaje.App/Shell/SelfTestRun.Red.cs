using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// La colaboración necesita <c>dathinel-red</c> (<c>modulos\red\</c> junto al exe de una distribución). En un exe publicado
    /// tiene que estar, arrancar como módulo, presentarse y no tener ninguna sala abierta: sin abrir red. Desde el
    /// repositorio, sin compilar Sala, no hace nada.
    /// </summary>
    private static async Task RedAsync()
    {
        bool published = ModuleCatalog.FindDevelopmentModules(AppContext.BaseDirectory) is null;
        string? path = FindRedProcess();
        if (path is null)
        {
            if (published)
            {
                throw new InvalidOperationException("Falta modulos\\red\\dathinel-red.exe: sin él la colaboración no funciona.");
            }

            return;
        }

        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        await using ModuleProcess process = await ModuleProcess.StartAsync(path, cancellationToken: limit.Token);
        if (process.Client.Module.Id != "dathinel-red" || process.Client.Module.Functions.All(f => f.Id != "abrir-sala"))
        {
            throw new InvalidOperationException("dathinel-red arrancó pero no se presentó como se esperaba.");
        }

        try
        {
            await process.Client.InvokeAsync("estado", cancellationToken: limit.Token);
            throw new InvalidOperationException("dathinel-red tiene una sala abierta sin que nadie la pidiera.");
        }
        catch (ModuleCallException ex) when (ex.Code == "no-abierta")
        {
            // Sin sala no hay nada abierto en la red: es lo esperado.
        }
    }

    private static string? FindRedProcess()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_RED") is { Length: > 0 } fromEnv && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "dathinel-red.exe"), Path.Combine(AppContext.BaseDirectory, "modulos", "red", "dathinel-red.exe") })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string release = Path.Combine(d.FullName, "externo", "sala", "red", "target", "release", "dathinel-red.exe");
            if (File.Exists(release) && File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                return release;
            }
        }

        return null;
    }
}
