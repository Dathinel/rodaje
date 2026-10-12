using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>Reloj que solo avanza cuando la prueba lo pide.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now += delta;
}

/// <summary>
/// Un <see cref="EditorSession"/> con diálogos de mentira, un <see cref="AutosaveService"/> sobre una carpeta temporal y
/// un reloj falso. Se crea dentro de <see cref="UiThread.Run(Func{Task})"/>.
/// </summary>
internal sealed class AutosaveHarness : IDisposable
{
    public AutosaveHarness()
    {
        Root = Temp.File("Autoguardado");
        Session = new EditorSession(new FakePlayback(), new FakeProber(), Dialogs, new SequentialIdGenerator());
        Session.Notified += (_, n) => Notifications.Add(n);
        Service = new AutosaveService(Session, Dialogs, Root, new AutosaveSchedule(), Clock);
    }

    public TempDirectory Temp { get; } = new();

    public string Root { get; }

    public FakeDialogs Dialogs { get; } = new();

    public ManualClock Clock { get; } = new();

    public EditorSession Session { get; }

    public AutosaveService Service { get; }

    public List<EditorNotification> Notifications { get; } = [];

    private readonly List<IDisposable> _open = [];

    /// <summary>Las copias de la sesión del servicio, de la más nueva a la más vieja.</summary>
    public IReadOnlyList<AutosaveSnapshot> Snapshots => AutosaveRecovery.ListSnapshots(Service.SessionDirectory!);

    /// <summary>Espera a que terminen las escrituras pedidas y a que el servicio anote el resultado en su hilo.</summary>
    public async Task Drain()
    {
        await Service.WhenWritesDone();
        await Task.Yield();
    }

    /// <summary>Lo que dice <c>sesion.json</c> de una carpeta de sesión: si hay cambios sin guardar.</summary>
    public static bool ReadUnsaved(string sessionDirectory)
    {
        string text = File.ReadAllText(Path.Combine(sessionDirectory, AutosaveSession.InfoFileName));
        return System.Text.Json.Nodes.JsonNode.Parse(text)!["unsavedChanges"]!.GetValue<bool>();
    }

    /// <summary>
    /// Deja en <see cref="Root"/> una sesión de otra ejecución que se cayó con cambios sin guardar: una copia por
    /// nombre, en ese orden. Devuelve su carpeta.
    /// </summary>
    public string CrashedSession(string? projectPath, params string[] names)
    {
        using AutosaveSession crashed = AutosaveSession.Begin(Root);
        foreach (string name in names)
        {
            Project copy = Session.Project.Clone();
            copy.Name = name;
            crashed.Write(copy, projectPath);
        }

        // Dispose suelta el bloqueo sin borrar nada, como el sistema con un proceso que se cae.
        return crashed.Directory;
    }

    /// <summary>Lo que el próximo arranque ofrecería recuperar; se suelta al terminar la prueba.</summary>
    public IReadOnlyList<RecoverableSession> FindRecoverable()
    {
        IReadOnlyList<RecoverableSession> found = AutosaveRecovery.FindRecoverable(Root);
        _open.AddRange(found);
        return found;
    }

    public void Dispose()
    {
        Service.Dispose();
        foreach (IDisposable item in _open)
        {
            item.Dispose();
        }

        Temp.Dispose();
    }
}
