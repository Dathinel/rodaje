using Rodaje.Core.Effects;

namespace Rodaje.Core.Model;

/// <summary>
/// Rutas de archivo que guarda el proyecto: las de los medios y los archivos sueltos (<c>file:</c>) de las opciones que
/// los admiten (la LUT de «Cargar .cube…», la imagen del barrido por luminancia y el video de una máscara de imagen).
/// En el archivo van relativas a su carpeta cuando se puede, para poder mover la carpeta entera. En memoria van todas
/// absolutas (las referencias <c>file:</c>, como las escribe <c>LutReference.ForFile</c>), y al abrir se resuelven contra
/// la carpeta del archivo: así no dependen de la carpeta del proyecto, que cambia con «Guardar como», ni lo que guarda la
/// pila de deshacer, y guardar y abrir devuelve lo mismo.
/// </summary>
/// <remarks>
/// Antes solo se volvían relativas las rutas de los medios. Un archivo suelto elegido con el proyecto sin guardar
/// quedaba absoluto para siempre (al mover la carpeta, la LUT se perdía) y uno relativo no se recalculaba al guardar
/// en otra carpeta: el proyecto nuevo apuntaba a <c>luts/…</c> junto a él, donde no había nada.
/// </remarks>
public static class ProjectFilePaths
{
    /// <summary>
    /// Resuelve contra <paramref name="projectDirectory"/> las rutas relativas del proyecto recién leído: las de los medios
    /// y las de las referencias <c>file:</c>. Una referencia que no se puede resolver queda como está.
    /// </summary>
    public static void MakeAbsolute(Project project, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        foreach (MediaRef media in project.Media)
        {
            media.Path = media.ResolvePath(projectDirectory);
        }

        RewriteFileOptions(project, path => Path.IsPathFullyQualified(path) ? null : AbsoluteFile(path, projectDirectory));
    }

    /// <summary>
    /// Copia para guardar en <paramref name="projectDirectory"/>, con las rutas relativas a esa carpeta cuando se puede
    /// (en otra unidad o en otro recurso de red quedan absolutas).
    /// </summary>
    /// <param name="project">El proyecto en memoria; no se modifica.</param>
    /// <param name="projectDirectory">Carpeta donde se va a guardar.</param>
    /// <param name="currentDirectory">
    /// Carpeta contra la que están escritas las referencias <c>file:</c> relativas que haya en memoria (la del archivo
    /// abierto hasta ahora), o <c>null</c> si no hay: entonces esas referencias se guardan como están. La sesión ya no
    /// deja referencias relativas en memoria; puede traerlas un autoguardado de una versión anterior.
    /// </param>
    public static Project ForSaving(Project project, string projectDirectory, string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        Project copy = project.Clone();
        foreach (MediaRef media in copy.Media)
        {
            if (Path.IsPathFullyQualified(media.Path))
            {
                // En otra unidad, GetRelativePath devuelve la ruta absoluta tal cual; una más larga de lo que admite
                // Windows (de un proyecto de la v1.0.0) también se guarda tal cual.
                try
                {
                    media.Path = Path.GetRelativePath(projectDirectory, media.Path);
                }
                catch (PathTooLongException)
                {
                }
            }
        }

        RewriteFileOptions(copy, path =>
        {
            string? absolute = Path.IsPathFullyQualified(path) ? path
                : currentDirectory is null ? null
                : AbsoluteFile(path, currentDirectory);
            if (absolute is null)
            {
                return null;
            }

            string relative = Path.GetRelativePath(projectDirectory, absolute);
            return Path.IsPathRooted(relative) ? absolute : relative;
        });
        return copy;
    }

    /// <summary>
    /// Si la opción <paramref name="option"/> de <paramref name="type"/> (efecto o transición) admite un archivo suelto:
    /// las referencias con <see cref="OptionDefinition.AllowsFile"/> y el video de la máscara de imagen.
    /// </summary>
    public static bool IsFileOption(string type, string option) =>
        (type == BuiltInEffects.Mask && option == BuiltInEffects.MaskOptions.Matte)
        || (EffectRegistry.Find(type)?.Options.Any(o => o.AllowsFile && o.Name == option) ?? false);

    private static string? AbsoluteFile(string path, string projectDirectory)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path, Path.GetFullPath(projectDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Una ruta que no se puede resolver se deja como está; quien la use dirá que falta el archivo.
            return null;
        }
    }

    /// <summary>
    /// Cambia la ruta de cada referencia <c>file:</c> por lo que devuelve <paramref name="rewrite"/> (con la ruta en el
    /// formato del sistema), salvo que devuelva <c>null</c>. Se guarda con <c>/</c>, como hace <c>LutReference</c>.
    /// </summary>
    private static void RewriteFileOptions(Project project, Func<string, string?> rewrite)
    {
        foreach (Sequence sequence in project.Sequences)
        {
            Rewrite(sequence.MasterEffects, rewrite);
            foreach (AudioBus bus in sequence.AudioBuses)
            {
                Rewrite(bus.Effects, rewrite);
            }

            foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
            {
                Rewrite(track.Effects, rewrite);
                foreach (Clip clip in track.Clips)
                {
                    Rewrite(clip.Effects, rewrite);
                }

                foreach (TransitionInstance transition in track.Transitions)
                {
                    RewriteOptions(transition.Type, transition.Options, rewrite);
                }
            }
        }
    }

    private static void Rewrite(List<EffectInstance> effects, Func<string, string?> rewrite)
    {
        foreach (EffectInstance effect in effects)
        {
            RewriteOptions(effect.Type, effect.Options, rewrite);
        }
    }

    private static void RewriteOptions(string type, Dictionary<string, string> options, Func<string, string?> rewrite)
    {
        foreach ((string name, string value) in options.ToList())
        {
            if (!OptionDefinition.IsFileReference(value) || !IsFileOption(type, name))
            {
                continue;
            }

            string path = value[OptionDefinition.FilePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (rewrite(path) is { } rewritten)
            {
                options[name] = OptionDefinition.FilePrefix + rewritten.Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }
}
