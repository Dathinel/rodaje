using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Paquetes;

namespace Rodaje.App.Services.Packages;

/// <summary>
/// Lo que un proyecto referencia de paquetes que no están instalados (LUT y transiciones de paquete). El proyecto abre
/// igual: la LUT no se aplica y la transición se ve como fundido cruzado, pero se avisa al abrirlo.
/// </summary>
public static class MissingContent
{
    /// <summary>Las referencias <c>paquete@versión/elemento</c> que no encuentran su elemento, sin repetir y en orden.</summary>
    public static IReadOnlyList<PackageReference> Find(Project project, PackageScan installed)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(installed);
        var missing = new SortedDictionary<string, PackageReference>(StringComparer.Ordinal);
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.AllTracks)
            {
                foreach (EffectInstance effect in track.Effects.Concat(track.Clips.SelectMany(c => c.Effects)))
                {
                    Check(effect.Type, effect.Options, installed, missing);
                }

                foreach (TransitionInstance transition in track.Transitions)
                {
                    Check(transition.Type, transition.Options, installed, missing);
                }
            }
        }

        return [.. missing.Values];
    }

    /// <summary>Texto del aviso: qué paquetes faltan y qué pasa mientras tanto.</summary>
    public static string Describe(IReadOnlyList<PackageReference> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        string packages = string.Join(", ", missing.Select(r => $"«{r.PackageId}»").Distinct());
        return $"Faltan {packages}. Mientras no se instalen, sus LUT no se aplican y sus transiciones se ven como fundido cruzado. Se instalan desde Complementos.";
    }

    /// <summary>Avisa en la sesión cada vez que se abre un proyecto al que le faltan paquetes. Desde el hilo de la interfaz.</summary>
    public static void Attach(EditorSession session, PackageService packages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(packages);
        session.ProjectOpened += (_, _) => _ = CheckAsync(session, packages);
    }

    /// <summary>Avisa si al proyecto abierto le faltan paquetes; si todavía no se leyó lo instalado, lo lee antes.</summary>
    public static async Task<IReadOnlyList<PackageReference>> CheckAsync(EditorSession session, PackageService packages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(packages);
        Project project = session.Project;
        PackageScan installed = packages.HasScanned ? packages.Installed : await packages.RefreshAsync();
        if (!ReferenceEquals(project, session.Project))
        {
            return [];
        }

        IReadOnlyList<PackageReference> missing = Find(project, installed);
        if (missing.Count > 0)
        {
            session.Notify(NotificationSeverity.Warning, "Faltan paquetes", Describe(missing));
        }

        return missing;
    }

    private static void Check(string type, Dictionary<string, string> options, PackageScan installed, SortedDictionary<string, PackageReference> missing)
    {
        if (EffectRegistry.Find(type) is not { } definition)
        {
            return;
        }

        foreach (OptionDefinition option in definition.Options)
        {
            if (option.Kind == OptionKind.PackageReference
                && options.TryGetValue(option.Name, out string? value)
                && PackageReference.TryParse(value, out PackageReference? reference)
                && !IsInstalled(reference, installed))
            {
                missing.TryAdd(value, reference);
            }
        }
    }

    private static bool IsInstalled(PackageReference reference, PackageScan installed) =>
        installed.Find(reference.PackageId) is { } package && package.Manifest.Items.Any(i => i.Id == reference.ItemId);
}
