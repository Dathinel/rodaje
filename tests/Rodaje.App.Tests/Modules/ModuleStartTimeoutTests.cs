using System.Diagnostics;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Modules;

/// <summary>
/// El tope de arranque de los módulos (<see cref="ModuleManagerOptions.StartTimeout"/>): su valor de fábrica y el camino
/// de un módulo que arranca pero no se conecta a tiempo.
/// </summary>
[Collection(ModuleProcessGroup.Name)]
public sealed class ModuleStartTimeoutTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose()
    {
        // El proceso recién matado puede tardar en soltar su carpeta.
        for (int i = 0; ; i++)
        {
            try
            {
                _root.Dispose();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < 30)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// 30 s, no menos que el saludo del protocolo (también 30 s desde la tarea 205): en Windows el primer arranque de un exe
    /// recién copiado pasa por el antivirus y por el arranque de .NET, y con el equipo cargado 10 s no alcanzaban.
    /// </summary>
    [Fact]
    public void El_tope_de_arranque_de_fabrica_es_de_30_s_y_supera_el_saludo_del_protocolo()
    {
        var options = new ModuleManagerOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), options.StartTimeout);
        Assert.True(options.StartTimeout >= options.ClientOptions.HandshakeTimeout);
    }

    /// <summary>
    /// Un módulo vivo que nunca abre la tubería falla con «no se conectó en 1 s» al vencer el tope corto inyectado, aunque el
    /// saludo del cliente dé 120 s: el tope de arranque es el que manda. El trabajo queda fallido y no queda proceso.
    /// </summary>
    [Fact]
    public async Task Un_modulo_que_no_se_conecta_falla_al_vencer_el_tope_de_arranque()
    {
        string dir = Path.Combine(_root.Path, "mudo");
        Directory.CreateDirectory(dir);
        // Sigue vivo dos minutos sin conectarse: CreateProcess abre un .cmd con cmd.exe.
        File.WriteAllText(Path.Combine(dir, "mudo.cmd"), "@ping -n 121 127.0.0.1 > nul\r\n");
        File.WriteAllText(Path.Combine(dir, "paquete.json"), """
            { "formatVersion": 1, "id": "mudo", "version": "1.0.0", "kind": "module", "name": "Mudo", "author": "Pruebas",
              "license": "GPL-3.0-only", "apps": ["rodaje"],
              "module": { "entry": "mudo.cmd", "protocol": 1, "functions": ["eco"], "vramMb": 0 } }
            """);

        var options = new ModuleManagerOptions
        {
            Roots = [new ModuleRoot(_root.Path, ModuleOrigin.Installed)],
            StartTimeout = TimeSpan.FromSeconds(1),
            ClientOptions = new ModuleClientOptions { HandshakeTimeout = TimeSpan.FromSeconds(120) },
        };
        await using var manager = new ModuleManager(options, null);
        manager.Refresh();

        Stopwatch watch = Stopwatch.StartNew();
        ModuleProtocolException error = await Assert.ThrowsAsync<ModuleProtocolException>(
            () => manager.InvokeAsync("mudo", "eco", null, "Eco").WaitAsync(TimeSpan.FromSeconds(60)));
        watch.Stop();

        Assert.Contains("no se conectó en 1 s", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.9), $"Falló antes del tope: {watch.ElapsedMilliseconds} ms.");
        Assert.Empty(manager.ProcessIds);
        ModuleJob job = Assert.Single(manager.Jobs.Jobs);
        Assert.Equal(ModuleJobState.Failed, job.State);
    }
}
