using System.Text;
using Rodaje.Core.Interchange.Edl;
using Rodaje.Core.Interchange.Fcpxml;
using Rodaje.Core.Interchange.Otio;
using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange;

/// <summary>
/// Una línea de tiempo leída de otro formato, con identificadores locales: <see cref="Project"/> lleva los medios y las
/// secuencias (las de los compuestos y <see cref="Sequence"/>, la principal). Es lo que entrega un lector y lo que
/// <see cref="Fidelity.Project"/> dice que debe entregar.
/// </summary>
public sealed record TimelineDraft(Project Project, Sequence Sequence);

/// <summary>
/// Lo que se añade al proyecto al importar, con identificadores nuevos: <see cref="Media"/> son solo los medios nuevos (los
/// clips pueden usar también medios que el proyecto ya tenía), <see cref="Compounds"/> va de la más interna hacia fuera.
/// </summary>
public sealed record InterchangeResult(IReadOnlyList<MediaRef> Media, IReadOnlyList<Sequence> Compounds, Sequence Sequence, InterchangeReport Report);

/// <summary>Un archivo que escribe un formato: <see cref="Suffix"/> vacío es el pedido; la EDL añade <c>_V2</c>, <c>_V3</c>… por pista.</summary>
public sealed record InterchangeOutput(string Suffix, string Text);

/// <summary>Importar y exportar líneas de tiempo en EDL, FCPXML y OpenTimelineIO (D-062).</summary>
public static class InterchangeFiles
{
    /// <summary>El formato por la extensión, o nulo si no es ninguno de los tres.</summary>
    public static InterchangeFormat? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".edl" => InterchangeFormat.Edl,
        ".fcpxml" => InterchangeFormat.Fcpxml,
        ".otio" => InterchangeFormat.Otio,
        _ => null,
    };

    public static string Extension(InterchangeFormat format) => format switch
    {
        InterchangeFormat.Edl => ".edl",
        InterchangeFormat.Fcpxml => ".fcpxml",
        _ => ".otio",
    };

    /// <summary>
    /// Lee un archivo y lo deja listo para añadir a <paramref name="target"/>, sin modificarlo. <paramref name="targetDirectory"/>
    /// es la carpeta del proyecto (sus rutas relativas se resuelven contra ella para reconocer los medios que ya tiene).
    /// Lanza <see cref="InterchangeException"/> si el archivo está roto o pasa de un presupuesto.
    /// </summary>
    public static InterchangeResult Read(string path, Project target, string? targetDirectory, InterchangeOptions options, IIdGenerator ids)
    {
        InterchangeFormat format = FormatOf(path) ?? throw new InterchangeException($"«{Path.GetFileName(path)}» no es una EDL, un FCPXML ni un OpenTimelineIO.");
        var info = new FileInfo(path);
        if (info.Length > options.Limits.MaxFileBytes)
        {
            throw new InterchangeException($"«{info.Name}» pasa de {options.Limits.MaxFileBytes >> 20} MB.");
        }

        string text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        return Parse(text, format, Path.GetDirectoryName(Path.GetFullPath(path)), target, targetDirectory, options, ids);
    }

    /// <summary>Como <see cref="Read"/> con el texto ya leído; <paramref name="baseDirectory"/> es la carpeta del archivo importado.</summary>
    public static InterchangeResult Parse(string text, InterchangeFormat format, string? baseDirectory, Project target, string? targetDirectory, InterchangeOptions options, IIdGenerator ids)
    {
        var report = new InterchangeReport();
        TimelineDraft draft = ReadDraft(text, format, baseDirectory, options, report);
        return SequenceBuilder.Build(draft, target, targetDirectory, ids, report);
    }

    /// <summary>Lo leído antes de fusionarlo con un proyecto, con identificadores locales.</summary>
    public static TimelineDraft ReadDraft(string text, InterchangeFormat format, string? baseDirectory, InterchangeOptions options, InterchangeReport report) => format switch
    {
        InterchangeFormat.Edl => EdlReader.Read(text, baseDirectory, options, report),
        InterchangeFormat.Fcpxml => FcpxmlReader.Read(text, baseDirectory, options, report),
        _ => OtioReader.Read(text, baseDirectory, options, report),
    };

    /// <summary>
    /// Los textos que escribiría <see cref="Write"/>, sin tocar el disco. El informe sale de <see cref="Fidelity.Project"/>,
    /// la misma regla que comprueban las pruebas de ida y vuelta: los escritores no informan por su cuenta.
    /// </summary>
    public static IReadOnlyList<InterchangeOutput> Serialize(Project project, Sequence sequence, string? projectDirectory, InterchangeFormat format, InterchangeOptions options, InterchangeReport report)
    {
        Fidelity.Project(project, sequence, format, options, report);
        return format switch
        {
            InterchangeFormat.Edl => EdlWriter.Write(project, sequence, projectDirectory, options),
            InterchangeFormat.Fcpxml => FcpxmlWriter.Write(project, sequence, projectDirectory, options),
            _ => OtioWriter.Write(project, sequence, projectDirectory, options),
        };
    }

    /// <summary>Escribe <paramref name="path"/> (y, en EDL, un archivo más por pista de video) y devuelve las rutas escritas.</summary>
    public static IReadOnlyList<string> Write(string path, Project project, Sequence sequence, string? projectDirectory, InterchangeFormat format, InterchangeOptions options, InterchangeReport report)
    {
        var written = new List<string>();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        foreach (InterchangeOutput output in Serialize(project, sequence, projectDirectory, format, options, report))
        {
            string file = Path.Combine(folder, stem + output.Suffix + extension);
            File.WriteAllText(file, output.Text, new UTF8Encoding(false));
            written.Add(file);
        }

        return written;
    }

    /// <summary>Solo el informe de pérdidas de exportar, sin escribir nada.</summary>
    public static InterchangeReport Preview(Project project, Sequence sequence, InterchangeFormat format, InterchangeOptions options)
    {
        var report = new InterchangeReport();
        Fidelity.Project(project, sequence, format, options, report);
        return report;
    }
}
