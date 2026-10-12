using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Orden de escritura de <see cref="AutosaveSession.Write"/>: <c>sesion.json</c> con <c>unsavedChanges: true</c> va
/// antes de la copia, así que un corte entre los dos nunca deja una copia nueva que no se ofrezca.
/// </summary>
public sealed class AutosaveWriteOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"Orden autoguardado ñ {Guid.NewGuid():N}");
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

    [Fact]
    public void Write_MarksUnsavedChangesBeforeTheCopyLandsOnDisk()
    {
        var fileSystem = new ObservingFileSystem();
        AutosaveSession session = Begin(fileSystem);
        session.MarkSaved("Corto", null);

        // Al llevar la copia a su nombre final, sesion.json ya dice que hay cambios sin guardar.
        fileSystem.OnMove = () => fileSystem.UnsavedAtMove = ReadUnsaved(session.Directory);
        session.Write(Named("Corto"), null);

        Assert.True(fileSystem.UnsavedAtMove);
        Assert.True(ReadUnsaved(session.Directory));
    }

    [Fact]
    public void Write_ThatFailsAfterSaving_StillOffersThePreviousCopy()
    {
        var fileSystem = new ObservingFileSystem();
        AutosaveSession session = Begin(fileSystem);
        session.Write(Named("Primera"), null);
        session.MarkSaved("Primera", null);

        // Se vuelve a editar y la copia nueva no llega al disco (corte de luz o disco lleno a mitad).
        fileSystem.OnMove = () => throw new IOException("Corte a mitad de la copia (simulado).");
        Assert.Throws<IOException>(() => session.Write(Named("Segunda"), null));
        session.Dispose();

        RecoverableSession found = Assert.Single(Find());
        Assert.Equal(1, found.Latest.Number);
        Assert.Equal("Primera", found.LoadNewest().Project.Name);
    }

    [Fact]
    public void MarkSaved_AfterWrite_LeavesNothingToOffer()
    {
        AutosaveSession session = Begin(new ObservingFileSystem());
        session.Write(Named("Corto"), null);
        session.MarkSaved("Corto", null);
        session.Dispose();

        Assert.False(ReadUnsaved(session.Directory));
        Assert.Empty(Find());
    }

    private AutosaveSession Begin(ProjectSerializer.SaveFileSystem fileSystem)
    {
        AutosaveSession session = AutosaveSession.Begin(_root, AutosaveSession.DefaultMaxVersions, DateTimeOffset.Now, fileSystem);
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

    private static bool ReadUnsaved(string directory)
    {
        string text = File.ReadAllText(Path.Combine(directory, AutosaveSession.InfoFileName));
        return JsonNode.Parse(text)!["unsavedChanges"]!.GetValue<bool>();
    }

    /// <summary>Sin esperas entre reintentos; avisa justo antes de dar a la copia su nombre final.</summary>
    private sealed class ObservingFileSystem : ProjectSerializer.SaveFileSystem
    {
        public Action? OnMove { get; set; }

        public bool? UnsavedAtMove { get; set; }

        public override IReadOnlyList<TimeSpan> RetryDelays => [];

        public override void Sleep(TimeSpan delay)
        {
        }

        public override void Move(string source, string destination)
        {
            OnMove?.Invoke();
            base.Move(source, destination);
        }

        public override void Replace(string source, string destination, string backup)
        {
            OnMove?.Invoke();
            base.Replace(source, destination, backup);
        }
    }
}
