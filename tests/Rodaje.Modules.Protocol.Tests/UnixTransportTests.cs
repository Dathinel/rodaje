using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>Una prueba que solo tiene sentido en Linux y macOS; en Windows sale omitida.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Solo Linux y macOS.";
    }
}

/// <summary>
/// El transporte del protocolo en Linux y macOS (D-066): socket en una carpeta 0700 propia y corta, regiones como archivos
/// 0600 que no se dejan suplantar, carpetas de editores muertos barridas y módulos que no sobreviven al editor.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class UnixTransportTests
{
    private const UnixFileMode Mode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Mode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [UnixFact]
    public void The_socket_lives_in_a_private_short_folder()
    {
        string path = ModuleProcess.NewPipeName();

        Assert.True(ModuleProcess.IsValidPipeName(path));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) <= UnixRuntime.SocketPathLimit, path);
        string session = Path.GetDirectoryName(path)!;
        Assert.Equal(Mode0700, File.GetUnixFileMode(session));
        Assert.Equal(Mode0700, File.GetUnixFileMode(Path.GetDirectoryName(session)!));
        Assert.Equal("e." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Path.GetFileName(session));
    }

    [UnixFact]
    public void Only_the_editors_socket_paths_are_accepted_by_a_module()
    {
        string session = UnixRuntime.SessionDirectory;
        Assert.True(ModuleProcess.IsValidPipeName(Path.Combine(session, "m.0123456789abcdef")));
        Assert.False(ModuleProcess.IsValidPipeName("Rodaje.Mod.pipe.0123456789abcdef0123456789abcdef"));
        Assert.False(ModuleProcess.IsValidPipeName("m.0123456789abcdef"));
        Assert.False(ModuleProcess.IsValidPipeName(Path.Combine(session, "m.0123456789ABCDEF")));
        Assert.False(ModuleProcess.IsValidPipeName(Path.Combine(session, "x.0123456789abcdef")));
        Assert.False(ModuleProcess.IsValidPipeName(session + "/../e.1/m.0123456789abcdef"));
        Assert.False(ModuleProcess.IsValidPipeName("/" + new string('a', 120) + "/m.0123456789abcdef"));
        Assert.False(ModuleProcess.IsValidPipeName(null));
    }

    [UnixFact]
    public void A_region_is_an_exclusive_0600_file_deleted_on_close()
    {
        string path;
        using (SharedMemoryRegion region = SharedMemoryRegion.Create(4096))
        {
            path = Path.Combine(UnixRuntime.SessionDirectory, region.Name);
            Assert.Equal(Mode0600, File.GetUnixFileMode(path));
            region.GetSpan(0, 4).Fill(7);
            using SharedMemoryRegion opened = SharedMemoryRegion.Open(region.Name);
            Assert.Equal(4096, opened.Capacity);
            Assert.Equal(7, opened.GetSpan(3, 1)[0]);
            Assert.Throws<IOException>(() => SharedMemoryRegion.CreateFile(UnixRuntime.SessionDirectory, region.Name, 16));
        }

        Assert.False(File.Exists(path));
    }

    [UnixFact]
    public void A_symlink_or_a_file_with_other_permissions_is_not_opened_as_a_region()
    {
        string directory = Directory.CreateTempSubdirectory("rodaje-region-").FullName;
        try
        {
            string target = Path.Combine(directory, "objetivo");
            File.WriteAllBytes(target, new byte[64]);
            File.SetUnixFileMode(target, Mode0600);
            File.CreateSymbolicLink(Path.Combine(directory, "Rodaje.Mod.enlace"), target);
            File.WriteAllBytes(Path.Combine(directory, "Rodaje.Mod.abierta"), new byte[64]);
            File.SetUnixFileMode(Path.Combine(directory, "Rodaje.Mod.abierta"), Mode0600 | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.Equal(ErrorCodes.BufferOutOfRange, Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open("Rodaje.Mod.enlace", directory)).Code);
            Assert.Equal(ErrorCodes.BufferOutOfRange, Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open("Rodaje.Mod.abierta", directory)).Code);
            Assert.Equal(ErrorCodes.BufferOutOfRange, Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open("Rodaje.Mod.no-esta", directory)).Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public void Folders_of_dead_editors_are_swept_and_live_ones_kept()
    {
        string root = Directory.CreateTempSubdirectory("rodaje-base-").FullName;
        try
        {
            using Process dead = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
            dead.WaitForExit();
            string deadFolder = Path.Combine(root, "e." + dead.Id.ToString(CultureInfo.InvariantCulture));
            string mine = Path.Combine(root, "e." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            string other = Path.Combine(root, "no-es-de-un-editor");
            foreach (string folder in (string[])[deadFolder, mine, other]) Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(deadFolder, "Rodaje.Mod.vieja"), "x");

            Assert.Equal(1, UnixRuntime.SweepOrphans(root));

            Assert.False(Directory.Exists(deadFolder));
            Assert.True(Directory.Exists(mine));
            Assert.True(Directory.Exists(other));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact]
    public void A_folder_of_another_owner_or_a_link_is_refused()
    {
        string root = Directory.CreateTempSubdirectory("rodaje-enlace-").FullName;
        try
        {
            string link = Path.Combine(root, "enlace");
            Directory.CreateSymbolicLink(link, root);
            Assert.Throws<ModuleProtocolException>(() => UnixRuntime.EnsurePrivateDirectory(link));
            // La raíz del sistema es de root: chmod falla con EPERM (salvo que las pruebas corran como root).
            if (UnixRuntime.GetEuid() != 0) Assert.Throws<ModuleProtocolException>(() => UnixRuntime.EnsurePrivateDirectory("/"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact]
    public async Task The_peer_is_identified_by_the_credentials_of_the_socket()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (editor)
        await using (module)
        {
            Assert.Equal(Environment.ProcessId, UnixRuntime.PeerProcessId(editor.SafePipeHandle));
        }
    }

    /// <summary>
    /// Muerte abrupta del editor: un <c>sh</c> hace de editor y lanza el módulo de eco, que se queda intentando conectar.
    /// Al matar el <c>sh</c> con SIGKILL, el módulo debe desaparecer enseguida (no a los 10 s de su espera de conexión).
    /// </summary>
    [UnixFact]
    public async Task A_module_does_not_outlive_an_editor_killed_abruptly()
    {
        string socket = Path.Combine(UnixRuntime.SessionDirectory, "m." + Guid.NewGuid().ToString("N")[..16]);
        ProcessStartInfo start = new("sh") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string a in (string[])["-c", "\"$0\" --rodaje-pipe \"$1\" & echo $!; wait", Support.EchoExe, socket]) start.ArgumentList.Add(a);
        using Process editor = Process.Start(start)!;
        int module = int.Parse((await editor.StandardOutput.ReadLineAsync().WaitAsync(Support.CiBound))!, CultureInfo.InvariantCulture);
        await Task.Delay(500);
        Assert.True(IsAlive(module), "El módulo no llegó a arrancar.");

        editor.Kill();
        await editor.WaitForExitAsync().WaitAsync(Support.CiBound);

        Stopwatch watch = Stopwatch.StartNew();
        while (IsAlive(module) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        Assert.False(IsAlive(module), $"El módulo {module} siguió vivo {watch.Elapsed.TotalSeconds:0.#} s después de matar al editor.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
