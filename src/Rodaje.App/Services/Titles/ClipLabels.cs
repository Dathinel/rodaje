using Rodaje.Core.Model;

namespace Rodaje.App.Services.Titles;

/// <summary>El nombre con que se muestra un clip: el de su medio o, en un título, su texto.</summary>
public static class ClipLabels
{
    /// <summary>Largo máximo del texto de un título usado como nombre.</summary>
    public const int MaxTitleName = 80;

    /// <summary>Lo que se muestra de un título sin texto.</summary>
    public const string EmptyTitle = "Texto";

    /// <summary>Por qué un título no admite velocidad, slip ni estirar.</summary>
    public const string NoMaterial = "Un título no tiene material: su duración se cambia recortando sus bordes.";

    /// <summary>Nombre del clip; <paramref name="fallback"/> si es un medio que ya no está.</summary>
    public static string Name(Project project, Clip clip, string fallback = "")
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.SequenceId is { } nested)
        {
            return "▣ " + (project.FindSequence(nested)?.Name ?? "Compuesto");
        }

        return clip.Generator is { } generator ? TitleName(generator) : project.FindMedia(clip.MediaId)?.Name ?? fallback;
    }

    /// <summary>La primera línea con texto, acortada; <see cref="EmptyTitle"/> si no hay.</summary>
    public static string TitleName(ClipGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (generator.Kind != GeneratorKinds.Text)
        {
            return generator.Kind;
        }

        foreach (string line in generator.Text.Split('\n'))
        {
            string trimmed = line.Replace('\t', ' ').Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= MaxTitleName ? trimmed : string.Concat(trimmed.AsSpan(0, MaxTitleName - 1), "…");
            }
        }

        return EmptyTitle;
    }

    /// <summary>Si el clip es un título (no tiene medio, miniaturas ni forma de onda).</summary>
    public static bool IsTitle(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Generator is not null;
    }
}
