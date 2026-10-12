using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Copias de autoguardado en disco: escritura atómica, rotación del historial y detección de un cierre inesperado.
/// Un cierre inesperado se simula con <see cref="AutosaveSession.Dispose"/>, que suelta el bloqueo sin borrar nada,
/// igual que hace el sistema con los archivos de un proceso que se cae.
/// </summary>
public sealed class AutosaveSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"Autoguardado ñ {Guid.NewGuid():N}");
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (IDisposable item in _open)
        {
            item.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Una carpeta temporal sobrante no afecta a otras pruebas.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual.
        }
    }

    private AutosaveSession Begin(int maxVersions = AutosaveSession.DefaultMaxVersions, ProjectSerializer.SaveFileSystem? fileSystem = null)
    {
        AutosaveSession session = AutosaveSession.Begin(_root, maxVersions, DateTimeOffset.Now, fileSystem ?? new NoDelay());
        _open.Add(session);
        return session;
    }

    private IReadOnlyList<RecoverableSession> Find()
    {
        IReadOnlyList<RecoverableSession> found = AutosaveRecovery.FindRecoverable(_root);
        _open.AddRange(found);
        return found;
    }

    private static Project Named(string name)
    {
        Project project = SerializationSamples.Rich();
        project.Name = name;
        return project;
    }

    // ---- Escritura atómica ----

    [Fact]
    public void Write_LeavesACompleteProjectAndNoTemporaryFiles()
    {
        AutosaveSession session = Begin();
        Project project = SerializationSamples.Rich();

        string path = session.Write(project, @"C:\Proyectos\Mi edición.rodaje");

        SerializationSamples.AssertDeepEqual(project, ProjectSerializer.Load(path));
        Assert.Empty(Directory.GetFiles(session.Directory, "*" + ProjectSerializer.TempSuffix));
        Assert.Equal(path, Assert.Single(session.Snapshots).Path);
    }

    [Fact]
    public void Write_ThatFailsMidway_KeepsThePreviousCopiesAndShowsNoPartialOne()
    {
        var fileSystem = new FailingMove();
        AutosaveSession session = Begin(fileSystem: fileSystem);
        string first = session.Write(Named("Primera"), null);
        string firstText = File.ReadAllText(first);

        fileSystem.FailNextMove = true;
        Assert.Throws<IOException>(() => session.Write(Named("Segunda"), null));

        AutosaveSnapshot only = Assert.Single(session.Snapshots);
        Assert.Equal(first, only.Path);
        Assert.Equal(firstText, File.ReadAllText(first));
        Assert.Empty(Directory.GetFiles(session.Directory, "*" + ProjectSerializer.TempSuffix));

        // La siguiente copia funciona y sigue la numeración.
        string third = session.Write(Named("Tercera"), null);
        Assert.Equal("Tercera", ProjectSerializer.Load(third).Name);
        Assert.Equal(2, session.Snapshots[0].Number);
    }

    [Fact]
    public void ListSnapshots_IgnoresTheTemporaryOfAnInterruptedWrite()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Completa"), null);
        File.WriteAllText(AutosaveSession.SnapshotPath(session.Directory, 2) + ProjectSerializer.TempSuffix, "{ cortado");

        AutosaveSnapshot only = Assert.Single(AutosaveRecovery.ListSnapshots(session.Directory));
        Assert.Equal(1, only.Number);
    }

    // ---- Rotación del historial ----

    [Fact]
    public void Write_KeepsOnlyTheNewestVersions()
    {
        AutosaveSession session = Begin(maxVersions: 3);
        for (int i = 1; i <= 5; i++)
        {
            session.Write(Named($"Versión {i}"), null);
        }

        IReadOnlyList<AutosaveSnapshot> snapshots = session.Snapshots;
        Assert.Equal([5, 4, 3], snapshots.Select(s => s.Number));
        Assert.Equal(["Versión 5", "Versión 4", "Versión 3"], snapshots.Select(s => ProjectSerializer.Load(s.Path).Name));
        Assert.Equal(3, Directory.GetFiles(session.Directory, AutosaveSession.SnapshotPrefix + "*").Length);
    }

    [Fact]
    public void Begin_RejectsZeroVersions() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AutosaveSession.Begin(_root, maxVersions: 0));

    // ---- Detección de cierre inesperado ----

    [Fact]
    public void CleanClose_LeavesNothingToRecover()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Cerrado bien"), null);

        session.Complete();

        Assert.False(Directory.Exists(session.Directory));
        Assert.Empty(Find());
    }

    [Fact]
    public void LiveSession_IsNotOfferedToAnotherWindow()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Abierto en otra ventana"), null);

        Assert.Empty(Find());
    }

    [Fact]
    public void UnexpectedClose_IsDetectedWithTheLatestCopy()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Antes"), null);
        session.Write(Named("Último cambio"), @"C:\Proyectos\Corto.rodaje");

        session.Dispose();

        RecoverableSession found = Assert.Single(Find());
        Assert.Equal(session.Directory, found.Directory);
        Assert.Equal("Último cambio", found.ProjectName);
        Assert.Equal(@"C:\Proyectos\Corto.rodaje", found.ProjectPath);
        Assert.Equal(2, found.Latest.Number);
        (Project project, AutosaveSnapshot used) = found.LoadNewest();
        Assert.Equal("Último cambio", project.Name);
        Assert.Equal(found.Latest, used);
    }

    [Fact]
    public void UnexpectedClose_AfterSaving_HasNothingToRecover()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Guardado después"), null);
        session.MarkSaved("Guardado después", @"C:\Proyectos\Guardado.rodaje");

        session.Dispose();

        Assert.Empty(Find());
    }

    [Fact]
    public void UnexpectedClose_EditingAgainAfterSaving_IsRecoverable()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Uno"), null);
        session.MarkSaved("Uno", null);
        session.Write(Named("Dos"), null);

        session.Dispose();

        Assert.Equal("Dos", Assert.Single(Find()).ProjectName);
    }

    [Fact]
    public void UnexpectedClose_WithoutAnyCopy_HasNothingToRecover()
    {
        AutosaveSession session = Begin();

        session.Dispose();

        Assert.Empty(Find());
    }

    [Fact]
    public void Clear_RemovesTheCopiesOfThePreviousProject()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Proyecto anterior"), null);

        session.Clear("Proyecto nuevo", null);
        session.Dispose();

        Assert.Empty(AutosaveRecovery.ListSnapshots(session.Directory));
        Assert.Empty(Find());
    }

    [Fact]
    public void OfferedSession_IsNotOfferedTwiceUntilReleased()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Caído"), null);
        session.Dispose();

        RecoverableSession first = Assert.Single(Find());
        Assert.Empty(Find());

        first.Dispose();
        RecoverableSession again = Assert.Single(Find());

        again.Discard();
        Assert.Empty(Find());
        Assert.NotEmpty(AutosaveRecovery.ListSnapshots(session.Directory));
    }

    [Fact]
    public void FindRecoverable_ListsTheMostRecentSessionFirst()
    {
        AutosaveSession older = Begin();
        string olderCopy = older.Write(Named("Viejo"), null);
        File.SetLastWriteTimeUtc(olderCopy, DateTime.UtcNow.AddHours(-2));
        AutosaveSession newer = Begin();
        newer.Write(Named("Nuevo"), null);
        older.Dispose();
        newer.Dispose();

        Assert.Equal(["Nuevo", "Viejo"], Find().Select(s => s.ProjectName));
    }

    [Fact]
    public void LoadNewest_FallsBackWhenTheLatestCopyIsDamaged()
    {
        AutosaveSession session = Begin();
        session.Write(Named("Sana"), null);
        string damaged = session.Write(Named("Dañada"), null);
        session.Dispose();
        File.WriteAllText(damaged, "{ no es un proyecto");

        (Project project, AutosaveSnapshot used) = Assert.Single(Find()).LoadNewest();

        Assert.Equal("Sana", project.Name);
        Assert.Equal(1, used.Number);
    }

    [Fact]
    public void LoadNewest_ThrowsWhenNoCopyOpens()
    {
        AutosaveSession session = Begin();
        string only = session.Write(Named("Única"), null);
        session.Dispose();
        File.WriteAllText(only, "basura");

        Assert.Throws<ProjectFormatException>(() => Assert.Single(Find()).LoadNewest());
    }

    // ---- Limpieza ----

    [Fact]
    public void Prune_RemovesOldAndEmptyDeadSessionsButNeverLiveOnes()
    {
        AutosaveSession live = Begin();
        live.Write(Named("Viva"), null);
        AutosaveSession recent = Begin();
        recent.Write(Named("Caída reciente"), null);
        recent.Dispose();
        AutosaveSession empty = Begin();
        empty.Dispose();

        int deleted = AutosaveRecovery.Prune(_root, AutosaveRecovery.DefaultMaxAge, DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(live.Directory));
        Assert.True(Directory.Exists(recent.Directory));
        Assert.False(Directory.Exists(empty.Directory));

        deleted = AutosaveRecovery.Prune(_root, AutosaveRecovery.DefaultMaxAge, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(live.Directory));
        Assert.False(Directory.Exists(recent.Directory));
    }

    [Fact]
    public void Prune_LeavesJustCreatedSessionsAlone()
    {
        AutosaveSession empty = Begin();
        empty.Dispose();

        Assert.Equal(0, AutosaveRecovery.Prune(_root, AutosaveRecovery.DefaultMaxAge));
        Assert.True(Directory.Exists(empty.Directory));
    }

    [Fact]
    public void FindRecoverable_WithoutTheFolder_FindsNothing() =>
        Assert.Empty(AutosaveRecovery.FindRecoverable(Path.Combine(_root, "no existe")));

    [Fact]
    public void Write_AfterComplete_Throws()
    {
        AutosaveSession session = Begin();
        session.Complete();

        Assert.Throws<ObjectDisposedException>(() => session.Write(Named("Tarde"), null));
    }

    private class NoDelay : ProjectSerializer.SaveFileSystem
    {
        public override IReadOnlyList<TimeSpan> RetryDelays => [];
    }

    private sealed class FailingMove : NoDelay
    {
        public bool FailNextMove { get; set; }

        public override void Move(string source, string destination)
        {
            if (FailNextMove)
            {
                FailNextMove = false;
                throw new IOException("Disco lleno (simulado).");
            }

            base.Move(source, destination);
        }
    }
}
