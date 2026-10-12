using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Relink;

/// <summary>
/// Volver a enlazar medios movidos o renombrados (función 32 de docs/pendientes.md): qué falta al abrir un proyecto, buscar
/// en una carpeta, comprobar un archivo elegido a mano y aplicarlo todo en un paso del historial. Sin WinUI: el diálogo
/// está en <c>Views/Relink</c>. El disco se lee fuera del hilo de la interfaz.
/// </summary>
public sealed class MediaRelinkService
{
    private readonly IMediaProber _prober;
    private readonly IRelinkFileSystem _fileSystem;
    private readonly Func<string, bool> _exists;

    public MediaRelinkService(IMediaProber prober, IRelinkFileSystem? fileSystem = null, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(prober);
        _prober = prober;
        _fileSystem = fileSystem ?? new DiskRelinkFileSystem(Probe);
        _exists = exists ?? File.Exists;
    }

    /// <summary>Opciones de la búsqueda en carpeta.</summary>
    public RelinkSearchOptions SearchOptions { get; init; } = new();

    /// <summary>Los datos de un archivo como medio, o <c>null</c> si no se puede usar.</summary>
    public RelinkProbe? Probe(string path)
    {
        MediaProbeResult result = _prober.Probe(path);
        if (!result.IsUsable)
        {
            return null;
        }

        var media = new MediaRef { Name = "-", Path = path };
        result.Info!.ApplyTo(media);
        return RelinkProbe.Of(media);
    }

    /// <summary>Los medios del proyecto cuyo archivo no está.</summary>
    public Task<IReadOnlyList<MediaRef>> FindMissingAsync(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        // Las rutas se copian antes: el proyecto solo se toca desde el hilo de la interfaz.
        List<MediaRef> media = [.. project.Media.Select(m => m.Clone())];
        return Task.Run(() =>
        {
            HashSet<string> absent = [.. MediaRelinkEdits.FindMissing(new Project { Media = media }, _exists).Select(m => m.Id)];
            IReadOnlyList<MediaRef> missing = [.. project.Media.Where(m => absent.Contains(m.Id))];
            return missing;
        });
    }

    /// <summary>Busca en <paramref name="folder"/> los archivos de <paramref name="missing"/>.</summary>
    public Task<RelinkSearchResult> SearchAsync(
        Project project, IReadOnlyList<MediaRef> missing, string folder, IProgress<int>? filesScanned = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(missing);
        Project copy = project.Clone();
        List<MediaRef> wanted = [.. missing.Select(m => copy.FindMedia(m.Id)).OfType<MediaRef>()];
        return Task.Run(
            () => MediaRelinkSearch.Search(copy, wanted, folder, _fileSystem, SearchOptions, filesScanned, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Comprueba un archivo elegido a mano para <paramref name="media"/>: el cambio a aplicar, o por qué no sirve.
    /// </summary>
    public async Task<(MediaRelinkTarget? Target, string? Problem)> CheckFileAsync(Project project, MediaRef media, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(path);
        RelinkProbe? probe = await Task.Run(() => Probe(path));
        if (probe is null)
        {
            return (null, $"«{Path.GetFileName(path)}» no se puede leer como video, audio o imagen.");
        }

        try
        {
            return (MediaRelinkEdits.TargetFor(project, media, path, probe, SearchOptions.DurationTolerance), null);
        }
        catch (EditException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Aplica los cambios en un solo paso del historial; <c>false</c> si no hay ninguno o la edición no se pudo.</summary>
    public static bool Apply(EditorSession session, IReadOnlyList<MediaRelinkTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Count > 0 && session.Execute(new RelinkMediaCommand(targets));
    }

    /// <summary>
    /// Al abrir un proyecto (también al arrancar con uno), si le faltan medios llama a <paramref name="show"/> con la lista.
    /// Desde el hilo de la interfaz.
    /// </summary>
    public void Attach(EditorSession session, Func<IReadOnlyList<MediaRef>, Task> show)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(show);
        session.ProjectOpened += (_, _) => _ = CheckOpenedAsync(session, show);
    }

    /// <summary>Lo que se hace al abrir: si faltan medios y el proyecto sigue siendo el mismo, se muestran.</summary>
    public async Task<IReadOnlyList<MediaRef>> CheckOpenedAsync(EditorSession session, Func<IReadOnlyList<MediaRef>, Task> show)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(show);
        Project project = session.Project;
        if (project.Media.Count == 0)
        {
            return [];
        }

        IReadOnlyList<MediaRef> missing = await FindMissingAsync(project);
        if (missing.Count == 0 || !ReferenceEquals(project, session.Project))
        {
            return [];
        }

        await show(missing);
        return missing;
    }

    /// <summary>Texto corto de lo que falta, para el aviso cuando no se puede abrir el diálogo o se cierra sin terminar.</summary>
    public static string Describe(IReadOnlyList<MediaRef> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return missing.Count == 1
            ? $"No se encuentra «{MediaRelinkSearch.FileName(missing[0].Path)}». Vuelve a enlazarlo para verlo y exportarlo."
            : $"No se encuentran {missing.Count} medios. Vuelve a enlazarlos para verlos y exportarlos.";
    }
}
