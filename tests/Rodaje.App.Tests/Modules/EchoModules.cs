using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Modules;

/// <summary>Una carpeta de módulos instalados con copias del módulo eco (cada una con su id y su vramMb).</summary>
internal sealed class EchoModules : IDisposable
{
    private static readonly string[] Files =
        ["Rodaje.Module.Echo.exe", "Rodaje.Module.Echo.dll", "Rodaje.Module.Echo.runtimeconfig.json", "Rodaje.Module.Echo.deps.json", "Rodaje.Modules.Protocol.dll"];

    private readonly TempDirectory _folder = new();

    public string Root => _folder.Path;

    public EchoModules Add(string id, int vramMb = 0, string version = "1.0.0")
    {
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        foreach (string file in Files)
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(dir, file), overwrite: true);
        }

        File.WriteAllText(Path.Combine(dir, "paquete.json"), $$"""
            { "formatVersion": 1, "id": "{{id}}", "version": "{{version}}", "kind": "module", "name": "Eco {{id}}", "author": "Pruebas",
              "license": "GPL-3.0-only", "apps": ["rodaje"],
              "module": { "entry": "Rodaje.Module.Echo.exe", "protocol": 1, "functions": ["eco", "contar", "copiar", "morir", "colgar", "fallar"], "vramMb": {{vramMb}} } }
            """);
        return this;
    }

    public ModuleManager Manager(ModuleManagerOptions? options = null, IModulePreview? preview = null)
    {
        options ??= new ModuleManagerOptions();
        var manager = new ModuleManager(options with { Roots = [new ModuleRoot(Root, ModuleOrigin.Installed)] }, preview);
        manager.Refresh();
        return manager;
    }

    // Un proceso recién matado puede tardar en soltar su exe.
    public void Dispose()
    {
        for (int i = 0; ; i++)
        {
            try
            {
                _folder.Dispose();
                return;
            }
            catch (UnauthorizedAccessException) when (i < 30)
            {
                Thread.Sleep(100);
            }
            catch (IOException) when (i < 30)
            {
                Thread.Sleep(100);
            }
        }
    }

    public static JsonElement Json(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// El proceso terminó o termina antes de <paramref name="timeout"/> (3 s si no se dice). Espera con
    /// <see cref="Process.WaitForExitAsync"/>: con <c>WaitForExit</c> dejaba bloqueado el hilo del pool de la prueba.
    /// </summary>
    public static async Task<bool> HasExitedAsync(int pid, TimeSpan? timeout = null)
    {
        Process p;
        try
        {
            p = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (p)
        {
            using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                await p.WaitForExitAsync(cancel.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>Congela el proceso entero (un módulo «colgado» de verdad: tampoco contesta el ping).</summary>
    public static void Suspend(int pid)
    {
        using Process p = Process.GetProcessById(pid);
        Assert.Equal(0, NtSuspendProcess(p.Handle));
    }

    [DllImport("ntdll.dll")]
#pragma warning disable SYSLIB1054, CA5392
    private static extern int NtSuspendProcess(IntPtr handle);
#pragma warning restore SYSLIB1054, CA5392
}

/// <summary>Cuenta cuántas veces se bajó la vista previa y si sigue baja.</summary>
internal sealed class CountingPreview : IModulePreview
{
    public int Lowered;
    public int Active;

    public IDisposable Lower()
    {
        Interlocked.Increment(ref Lowered);
        Interlocked.Increment(ref Active);
        return new Restore(this);
    }

    private sealed class Restore(CountingPreview owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref owner.Active);
            }
        }
    }
}
