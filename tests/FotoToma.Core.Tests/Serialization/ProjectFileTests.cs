using System.Text;
using FotoToma.Core.Model;
using FotoToma.Core.Serialization;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Serialization;

/// <summary>Guardado y apertura en disco, en una carpeta con espacios, tildes y eñe.</summary>
public sealed class ProjectFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoToma pruebas", $"Prueba con espacios ñ {Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Una carpeta temporal sobrante no afecta a otras pruebas.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual: el antivirus puede tener abierto un archivo recién escrito.
        }
    }

    private string PathFor(string name = "Mi edición final") => Path.Combine(_dir, name + ProjectSerializer.FileExtension);

    [Fact]
    public void FileExtension_IsFtoma() => Assert.Equal(".ftoma", ProjectSerializer.FileExtension);

    [Fact]
    public void Save_NewFile_WritesUtf8WithoutBomAndNoLeftovers()
    {
        string path = PathFor();
        Project project = SerializationSamples.Rich();

        ProjectSerializer.Save(project, path);

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(ProjectSerializer.Serialize(project) + "\n", Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Save_Overwrite_KeepsPreviousVersionAsBak()
    {
        string path = PathFor();
        Project first = SerializationSamples.Rich();
        ProjectSerializer.Save(first, path);
        string firstText = File.ReadAllText(path);

        Project second = SerializationSamples.Rich();
        second.Name = "Segunda versión";
        ProjectSerializer.Save(second, path);

        Assert.Equal(firstText, File.ReadAllText(path + ".bak"));
        Assert.Equal("Segunda versión", ProjectSerializer.Load(path).Name);
        Assert.Equal("Vacaciones en Cartagena — edición ñ", ProjectSerializer.Load(path + ".bak").Name);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(
            [Path.GetFileName(path), Path.GetFileName(path) + ".bak"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Save_OverwritesStaleTempFromInterruptedSave()
    {
        string path = PathFor();
        File.WriteAllText(path + ".tmp", "basura de un guardado interrumpido");

        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        Assert.False(File.Exists(path + ".tmp"));
        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), ProjectSerializer.Load(path));
    }

    [Fact]
    public void Save_RelativePath_IsResolvedAndWorks()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, PathFor("relativo"));
        ProjectSerializer.Save(SerializationSamples.Rich(), relative);
        Assert.True(File.Exists(PathFor("relativo")));
    }

    [Fact]
    public void Save_Failure_RemovesTempAndRethrows()
    {
        // El destino es una carpeta: el temporal se escribe, pero no se puede mover sobre ella.
        string path = Path.Combine(_dir, "carpeta.ftoma");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_Failure_WhenTempCannotBeDeleted_StillRethrowsOriginalError()
    {
        // El temporal es una carpeta no vacía: no se puede crear ni borrar como archivo.
        string path = PathFor("bloqueado");
        Directory.CreateDirectory(path + ".tmp");
        File.WriteAllText(Path.Combine(path + ".tmp", "dentro.txt"), "x");

        var fs = new NoDelay();
        Assert.ThrowsAny<UnauthorizedAccessException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path, fs));
        Assert.Equal(4, fs.Sleeps.Count);
        Assert.True(Directory.Exists(path + ".tmp"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_TempLockedByAnotherProgram_ThrowsAndKeepsExistingFile()
    {
        string path = PathFor("en uso");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);
        string before = File.ReadAllText(path);

        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var fs = new NoDelay();
            Assert.ThrowsAny<IOException>(() => ProjectSerializer.Save(new Project { Id = "otro" }, path, fs));
            Assert.Equal(4, fs.Sleeps.Count);
        }

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Load_PathWithSpacesAndAccents_RoundTrips()
    {
        string path = PathFor("Canción del año ñandú");
        ProjectSerializer.Save(SerializationSamples.Rich(), path);

        Project loaded = ProjectSerializer.Load(path);

        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), loaded);
    }

    [Fact]
    public void Load_AcceptsUtf8Bom()
    {
        string path = PathFor("con bom");
        File.WriteAllText(path, ProjectSerializer.Serialize(SerializationSamples.Rich()), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("project-1", ProjectSerializer.Load(path).Id);
    }

    [Fact]
    public void Load_CorruptFile_NamesTheFileAndKeepsProblems()
    {
        string path = PathFor("dañado");
        File.WriteAllText(path, ProjectSerializer.Serialize(SerializationSamples.Rich())[..200]);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.StartsWith("No se pudo abrir «dañado.ftoma».", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no es JSON bien formado", ex.Message, StringComparison.Ordinal);
        Assert.IsType<ProjectFormatException>(ex.InnerException);
    }

    [Fact]
    public void Load_InvalidProject_KeepsProblemList()
    {
        Project project = SerializationSamples.Rich();
        project.Media.Clear();
        string path = PathFor("sin medios");
        ProjectSerializer.Save(project, path);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Equal(4, ex.Problems.Count);
    }

    [Fact]
    public void Load_NotUtf8_ThrowsFormatException()
    {
        string path = PathFor("binario");
        File.WriteAllBytes(path, [0x7B, 0xC3, 0x28, 0xFF, 0x7D]);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.Contains("no es texto UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ProjectSerializer.Load(PathFor("no existe")));
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Save(null!, PathFor()));
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.Save(new Project(), null!));
        Assert.Throws<ArgumentException>(() => ProjectSerializer.Save(new Project(), " "));
        Assert.Throws<ArgumentException>(() => ProjectSerializer.Load(""));
    }

    [Fact]
    public void Load_ExtremeTimes_ThrowsFormatExceptionNamingTheFile()
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].VideoTracks[0].Clips[0].Start = Time.FromTicks(long.MaxValue - 10);
        project.Sequences[0].VideoTracks[0].Clips[0].SourceIn = Time.FromTicks(long.MinValue);
        string path = PathFor("tiempos extremos");
        ProjectSerializer.Save(project, path);

        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(path));

        Assert.StartsWith("No se pudo abrir «tiempos extremos.ftoma».", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ex.Problems, p => p.EndsWith("tiene tiempos fuera de rango.", StringComparison.Ordinal));
    }

    // --- Fallos del reemplazo atómico, simulados con un sistema de archivos de prueba ---

    /// <summary>
    /// Imita a File.Replace cuando falla con ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: el original ya se movió a
    /// .bak, pero el temporal no llegó a su sitio. Opcionalmente, el renombrado de recuperación también falla.
    /// </summary>
    private sealed class ReplaceFailsAfterBackup(Exception? moveFailure) : NoDelay
    {
        public int Moves { get; private set; }

        public override void Replace(string source, string destination, string backup)
        {
            File.Move(destination, backup, overwrite: true);
            throw new IOException("No se pudo mover el archivo de reemplazo (simulado).");
        }

        public override void Move(string source, string destination)
        {
            Moves++;
            if (moveFailure is not null)
            {
                throw moveFailure;
            }

            base.Move(source, destination);
        }
    }

    /// <summary>File.Replace falla sin tocar nada: el original sigue en su ruta.</summary>
    private sealed class ReplaceFailsCleanly(Exception failure) : NoDelay
    {
        public override void Replace(string source, string destination, string backup) => throw failure;
    }

    private (string Path, string OldText, Project Second) SavedTwiceSetup(string name)
    {
        string path = PathFor(name);
        ProjectSerializer.Save(SerializationSamples.Rich(), path);
        Project second = SerializationSamples.Rich();
        second.Name = "Versión nueva";
        return (path, File.ReadAllText(path), second);
    }

    [Fact]
    public void Save_ReplaceFailsAfterBackup_RecoversByRenamingTemp()
    {
        (string path, string oldText, Project second) = SavedTwiceSetup("recuperable");
        var fs = new ReplaceFailsAfterBackup(moveFailure: null);

        ProjectSerializer.Save(second, path, fs);

        Assert.Equal(1, fs.Moves);
        Assert.Equal("Versión nueva", ProjectSerializer.Load(path).Name);
        Assert.Equal(oldText, File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public static TheoryData<Exception> RecoveryFailures => new()
    {
        new IOException("renombrado bloqueado (simulado)"),
        new UnauthorizedAccessException("sin permiso (simulado)"),
    };

    [Theory]
    [MemberData(nameof(RecoveryFailures))]
    public void Save_ReplaceAndRecoveryFail_KeepsTempAndSaysWhereItIs(Exception moveFailure)
    {
        (string path, string oldText, Project second) = SavedTwiceSetup("irrecuperable");

        var ex = Assert.Throws<IOException>(() => ProjectSerializer.Save(second, path, new ReplaceFailsAfterBackup(moveFailure)));

        // El temporal es la única copia de la versión nueva: se conserva intacto y el mensaje lo nombra.
        Assert.True(File.Exists(path + ".tmp"));
        Assert.Equal("Versión nueva", ProjectSerializer.Load(path + ".tmp").Name);
        Assert.Equal(oldText, File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path));
        Assert.Contains($"«{path}.tmp»", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"«{path}.bak»", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cámbiale el nombre", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(simulado)", ex.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void Save_ReplaceFailsWithOriginalInPlace_DeletesTempAndRethrows()
    {
        (string path, string oldText, Project second) = SavedTwiceSetup("intacto");
        var failure = new IOException("reemplazo rechazado (simulado)");

        var ex = Assert.Throws<IOException>(() => ProjectSerializer.Save(second, path, new ReplaceFailsCleanly(failure)));

        Assert.Same(failure, ex);
        Assert.Equal(oldText, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".bak"));
    }

    // --- Reintentos ante bloqueos pasajeros ---

    private static IOException Sharing() => new("en uso (simulado)", unchecked((int)0x80070020));

    /// <summary>Sin esperas reales; anota las que se pidieron.</summary>
    private class NoDelay : ProjectSerializer.SaveFileSystem
    {
        public List<TimeSpan> Sleeps { get; } = [];

        public override void Sleep(TimeSpan delay) => Sleeps.Add(delay);
    }

    /// <summary>Replace o Move fallan <paramref name="failures"/> veces con <paramref name="error"/> y luego funcionan.</summary>
    private sealed class FlakyFileSystem(int failures, Func<Exception> error) : NoDelay
    {
        public int Calls { get; private set; }

        public override void Replace(string source, string destination, string backup)
        {
            Fail();
            base.Replace(source, destination, backup);
        }

        public override void Move(string source, string destination)
        {
            Fail();
            base.Move(source, destination);
        }

        private void Fail()
        {
            if (++Calls <= failures)
            {
                throw error();
            }
        }
    }

    [Fact]
    public void RetryPolicy_DefaultIsFiveAttemptsWithBackoff()
    {
        Assert.Equal(
            [50, 100, 200, 400],
            ProjectSerializer.SaveFileSystem.Real.RetryDelays.Select(d => (int)d.TotalMilliseconds));
        ProjectSerializer.SaveFileSystem.Real.Sleep(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_TransientSharingViolation_IsRetriedAndSucceeds(bool fileExists)
    {
        string path = PathFor(fileExists ? "reintento reemplazo" : "reintento nuevo");
        if (fileExists)
        {
            ProjectSerializer.Save(new Project { Id = "vieja" }, path);
        }

        var fs = new FlakyFileSystem(2, Sharing);
        ProjectSerializer.Save(SerializationSamples.Rich(), path, fs);

        Assert.Equal(3, fs.Calls);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], fs.Sleeps);
        Assert.Equal("project-1", ProjectSerializer.Load(path).Id);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_TransientAccessDenied_IsRetried()
    {
        string path = PathFor("antivirus");
        var fs = new FlakyFileSystem(1, () => new UnauthorizedAccessException("escaneando (simulado)"));

        ProjectSerializer.Save(SerializationSamples.Rich(), path, fs);

        Assert.Equal(2, fs.Calls);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_PersistentLockViolation_GivesUpAfterFiveAttempts()
    {
        string path = PathFor("bloqueo persistente");
        var fs = new FlakyFileSystem(int.MaxValue, () => new IOException("bloqueado (simulado)", unchecked((int)0x80070021)));

        Assert.Throws<IOException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path, fs));

        Assert.Equal(5, fs.Calls);
        Assert.Equal(4, fs.Sleeps.Count);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_OtherIOError_IsNotRetried()
    {
        string path = PathFor("sin reintento");
        var fs = new FlakyFileSystem(int.MaxValue, () => new IOException("disco lleno (simulado)", unchecked((int)0x80070070)));

        Assert.Throws<IOException>(() => ProjectSerializer.Save(SerializationSamples.Rich(), path, fs));

        Assert.Equal(1, fs.Calls);
        Assert.Empty(fs.Sleeps);
    }

    [Fact]
    public void IsTransient_OnlySharingLockAndAccessDenied()
    {
        Assert.True(ProjectSerializer.IsTransient(Sharing()));
        Assert.True(ProjectSerializer.IsTransient(new IOException("x", unchecked((int)0x80070021))));
        Assert.True(ProjectSerializer.IsTransient(new UnauthorizedAccessException()));
        Assert.False(ProjectSerializer.IsTransient(new IOException("x")));
        Assert.False(ProjectSerializer.IsTransient(new FileNotFoundException()));
        Assert.False(ProjectSerializer.IsTransient(new InvalidOperationException()));
    }
}
