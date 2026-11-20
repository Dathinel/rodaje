using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// La fotografía del proyecto abierto para el invitado que entra: una copia sin rutas del anfitrión y la lista de medios con su
/// SHA-256. La copia se toma en el hilo de la interfaz (<paramref name="onUi"/>); calcular los SHA-256 y escribir va fuera.
/// </summary>
public sealed class SessionProjectSource(EditorSession session, Func<Func<(Project Copy, string Name)>, Task<(Project Copy, string Name)>> onUi, MediaManifestBuilder? builder = null) : ICollabProjectSource
{
    private readonly MediaManifestBuilder _builder = builder ?? new MediaManifestBuilder();

    public async Task<ProjectSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        (Project copy, string name) = await onUi(() => (session.Project.Clone(), session.ProjectName)).ConfigureAwait(false);
        IReadOnlyList<MediaManifestEntry> media = await _builder.BuildAsync(copy.Media, m => m.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
        ProjectScrub.Scrub(copy);
        return new ProjectSnapshot(name, Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(copy)), 0, media);
    }
}
