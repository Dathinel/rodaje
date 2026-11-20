using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Quita de un proyecto las rutas del equipo de quien lo mandó (o de un anfitrión que no sea de fiar): los medios quedan con una
/// ruta relativa sin significado (<c>medios\m1</c>) y las referencias a archivos sueltos (la LUT de «Cargar .cube…», la imagen
/// de un barrido, el video de una máscara) se quitan. Sin esto, abrir el proyecto de otro resolvería sus rutas, y una ruta de red
/// (<c>\\equipo\recurso\x.mp4</c>) haría que Windows se conecte al equipo de quien manda.
/// </summary>
/// <remarks>Interino: lo reemplaza <c>ProjectWire</c> de Core (B10) cuando se una; la prueba de aquí sigue valiendo como contrato.</remarks>
public static class ProjectScrub
{
    public const string MediaFolder = "medios";

    /// <summary>Limpia <paramref name="project"/> en su sitio; devuelve cuántas referencias a archivos sueltos se quitaron.</summary>
    public static int Scrub(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int removed = 0;
        for (int i = 0; i < project.Media.Count; i++)
        {
            project.Media[i].Path = PlaceholderFor(project.Media[i], i);
        }

        foreach (Sequence sequence in project.Sequences)
        {
            removed += Remove(sequence.MasterEffects);
            foreach (AudioBus bus in sequence.AudioBuses)
            {
                removed += Remove(bus.Effects);
            }

            foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
            {
                removed += Remove(track.Effects);
                foreach (Clip clip in track.Clips)
                {
                    removed += Remove(clip.Effects);
                }

                foreach (TransitionInstance transition in track.Transitions)
                {
                    removed += Remove(transition.Type, transition.Options);
                }
            }
        }

        return removed;
    }

    /// <summary>Una ruta relativa que no existe ni apunta fuera de la carpeta del proyecto.</summary>
    public static string PlaceholderFor(MediaRef media, int index)
    {
        string safe = new([.. media.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(48)]);
        return MediaFolder + Path.DirectorySeparatorChar + (safe.Length > 0 ? safe : "m" + index);
    }

    private static int Remove(List<EffectInstance> effects) => effects.Sum(e => Remove(e.Type, e.Options));

    private static int Remove(string type, Dictionary<string, string> options)
    {
        int removed = 0;
        foreach ((string name, string value) in options.ToList())
        {
            if (OptionDefinition.IsFileReference(value) && ProjectFilePaths.IsFileOption(type, name))
            {
                options.Remove(name);
                removed++;
            }
        }

        return removed;
    }
}
