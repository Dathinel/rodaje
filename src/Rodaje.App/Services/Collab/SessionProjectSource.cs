using System.Text;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// La fotografía del proyecto para el invitado que entra: lo confirmado de la sala (sin rutas, con sus versiones y su orden,
/// que da <paramref name="wire"/>) y la lista de medios con su SHA-256, sacada de los medios del proyecto abierto (que sí
/// conocen su ruta en este equipo). Los medios se leen en el hilo de la interfaz (<paramref name="onUi"/>); calcular los
/// SHA-256 va fuera.
/// </summary>
public sealed class SessionProjectSource(EditorSession session, Func<Func<(List<MediaRef> Media, string Name)>, Task<(List<MediaRef> Media, string Name)>> onUi, Func<(string Json, long Seq)> wire, MediaManifestBuilder? builder = null) : ICollabProjectSource
{
    private readonly MediaManifestBuilder _builder = builder ?? new MediaManifestBuilder();

    public async Task<ProjectSnapshot> CaptureAsync(IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        (List<MediaRef> media, string name) = await onUi(() => ([.. session.Project.Media.Select(m => m.Clone())], session.ProjectName)).ConfigureAwait(false);
        (string json, long seq) = wire();
        IReadOnlyList<MediaManifestEntry> manifest = await _builder.BuildAsync(media, m => m.Path, progress, cancellationToken).ConfigureAwait(false);
        return new ProjectSnapshot(name, Encoding.UTF8.GetBytes(json), seq, manifest);
    }
}
