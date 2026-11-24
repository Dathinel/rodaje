using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// El cierre inesperado de verdad: otro proceso (esta misma prueba, lanzada con <c>dotnet test</c>) edita, deja su copia y
/// muere con <see cref="Process.Kill()"/>, sin <c>finally</c> ni <c>Dispose</c>. Las demás pruebas simulan la caída
/// soltando el bloqueo con <c>Dispose</c>; aquí lo suelta el sistema al matar el proceso.
/// </summary>
public class AutosaveKillTests
{
    private const string ChildVariable = "RODAJE_AUTOGUARDADO_HIJO";
    private const string ReadyFile = "hijo-listo.txt";
    private const string Edited = "Corto sin guardar";

    [Fact]
    public async Task Recuperar_TrasMatarElProcesoQueEditaba()
    {
        using var temp = new TempDirectory();
        string root = temp.File("Autoguardado");
        Directory.CreateDirectory(root);
        using Process host = StartChild(root, temp.Path);
        try
        {
            string ready = Path.Combine(root, ReadyFile);
            var watch = Stopwatch.StartNew();
            while (!File.Exists(ready) && !host.HasExited && watch.Elapsed < TimeSpan.FromMinutes(4))
            {
                await Task.Delay(100);
            }

            Assert.True(File.Exists(ready), $"El hijo no llegó a dejar su copia (salió: {host.HasExited}).");
            int pid = int.Parse(await File.ReadAllTextAsync(ready), CultureInfo.InvariantCulture);

            // Vivo, su sesión no se ofrece (es otra ventana abierta).
            Assert.Empty(AutosaveRecovery.FindRecoverable(root));

            using (Process child = Process.GetProcessById(pid))
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
        finally
        {
            KillQuietly(host);
        }

        UiThread.Run(async () =>
        {
            using var h = new AutosaveHarness(root);
            h.Dialogs.SavePath = temp.File("recuperado.rodaje");
            h.Service.Start();

            await h.Service.OfferRecoveryAsync();
            EditorNotification offer = Assert.Single(h.Notifications);
            Assert.Contains(Edited, offer.Message, StringComparison.Ordinal);
            Task<EditorNotification> outcome = h.NotificationAsync(n => n.Severity != NotificationSeverity.Warning);
            offer.Action!.Run();
            EditorNotification done = await outcome;

            Assert.True(done.Severity == NotificationSeverity.Success, $"Recuperar terminó con «{done.Title}»: {done.Message}");
            Assert.Equal(Edited, h.Session.ProjectName);
            Assert.Equal(Edited, ProjectSerializer.Load(temp.File("recuperado.rodaje")).Name);
            Assert.Empty(h.FindRecoverable());
        });
    }

    /// <summary>Solo como proceso hijo de la prueba anterior: edita, deja una copia y espera a que lo maten.</summary>
    [ChildFact]
    public void Hijo_EditaDejaSuCopiaYEsperaAQueLoMaten() => UiThread.Run(async () =>
    {
        string root = Environment.GetEnvironmentVariable(ChildVariable)!;
        using var h = new AutosaveHarness(root);
        Assert.True(h.Service.Start());
        Assert.True(h.Session.Execute(new RenameProjectCommand(Edited)));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Snapshots);

        await File.WriteAllTextAsync(Path.Combine(root, ReadyFile), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(TimeSpan.FromMinutes(10));
    }, TimeSpan.FromMinutes(11));

    private static Process StartChild(string root, string workingDirectory)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
            : Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot ? Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            : "dotnet";
        var info = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string a in (string[])["test", typeof(AutosaveKillTests).Assembly.Location, "--filter", $"FullyQualifiedName~{nameof(Hijo_EditaDejaSuCopiaYEsperaAQueLoMaten)}"])
        {
            info.ArgumentList.Add(a);
        }

        info.Environment[ChildVariable] = root;
        Process process = Process.Start(info) ?? throw new InvalidOperationException("No arrancó dotnet test.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class ChildFactAttribute : FactAttribute
    {
        public ChildFactAttribute() => Skip = Environment.GetEnvironmentVariable(ChildVariable) is null ? "Solo como proceso hijo de la prueba de matar el autoguardado." : null;
    }
}
