using System.Text;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

internal enum MatrixStep
{
    Import,
    Thumbnails,
    Waveform,
    Play,
    Seek,
    Reverse,
    ExportH264,
    ExportLossless,
}

/// <summary>El resultado de un caso: un texto por paso y las fallas.</summary>
internal sealed class MatrixRow(MatrixCase c)
{
    private readonly Dictionary<MatrixStep, string> _cells = [];

    public MatrixCase Case { get; } = c;
    public List<string> Failures { get; } = [];
    public List<string> Notes { get; } = [];
    public bool Unsupported { get; set; }
    public string? NotGeneratedReason { get; private set; }

    public void Set(MatrixStep step, string text)
    {
        if (!_cells.TryGetValue(step, out string? current) || !current.StartsWith("FALLA", StringComparison.Ordinal))
        {
            _cells[step] = text;
        }
    }

    public void Fail(MatrixStep step, string text) => Fail(step.ToString(), text, step);

    public void Fail(string where, string text, MatrixStep? step = null)
    {
        Failures.Add($"{Case.Name} [{where}]: {text}");
        if (step is { } s)
        {
            _cells[s] = "FALLA: " + text;
        }
        else
        {
            Notes.Add($"FALLA en {where}: {text}");
        }
    }

    public void Note(string text) => Notes.Add(text);

    public void NotGenerated(string reason) => NotGeneratedReason = reason.Split('\n')[0];

    public string ToMarkdown()
    {
        string Cell(MatrixStep step) => _cells.TryGetValue(step, out string? text) ? text.Replace('|', '/').Replace('\n', ' ') : "—";
        string status = NotGeneratedReason is not null ? "no generable"
            : Failures.Count > 0 ? "FALLA"
            : Unsupported ? "rechazado con motivo"
            : "ok";
        return $"| {Case.Name} | {Case.Container} | {Case.VideoLabel} | {Case.AudioLabel} | {status} | {Cell(MatrixStep.Import)} | {Cell(MatrixStep.Thumbnails)} | {Cell(MatrixStep.Waveform)} | "
            + $"{Cell(MatrixStep.Play)} | {Cell(MatrixStep.Seek)} | {Cell(MatrixStep.Reverse)} | {Cell(MatrixStep.ExportH264)} | {Cell(MatrixStep.ExportLossless)} |"
            + (NotGeneratedReason is { } r ? $" {r} |" : (Notes.Count > 0 && Failures.Count > 0 ? $" {string.Join(" / ", Notes.Where(n => n.StartsWith("FALLA", StringComparison.Ordinal)))} |" : string.Empty));
    }
}

/// <summary>Junta las filas en <c>TestResults\matriz\tabla.md</c>, en el orden de <see cref="MatrixCases.All"/>.</summary>
internal static class MatrixTable
{
    private static readonly Lock Gate = new();

    public static string Folder => Path.Combine(Path.GetDirectoryName(TestMediaGenerator.DefaultCacheDirectory)!, "matriz");

    public static void Save(MatrixRow row)
    {
        lock (Gate)
        {
            string rows = Path.Combine(Folder, "filas");
            Directory.CreateDirectory(rows);
            File.WriteAllText(Path.Combine(rows, row.Case.Name + ".md"), row.ToMarkdown(), Encoding.UTF8);
            var table = new StringBuilder()
                .AppendLine("| Caso | Contenedor | Video | Audio | Estado | Importar | Miniaturas | Onda | Reproducir | Seek | Reversa | Exportar H.264 | Exportar sin pérdida |")
                .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (MatrixCase c in MatrixCases.All)
            {
                string file = Path.Combine(rows, c.Name + ".md");
                if (File.Exists(file))
                {
                    table.AppendLine(File.ReadAllText(file));
                }
            }

            File.WriteAllText(Path.Combine(Folder, "tabla.md"), table.ToString(), Encoding.UTF8);
        }
    }
}
