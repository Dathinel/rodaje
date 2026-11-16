using System.Globalization;
using System.Text;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange;

public enum IssueSeverity
{
    /// <summary>Un dato que se normalizó sin cambiar lo que se ve ni lo que se oye (una tasa 29,97 escrita como 30000/1001).</summary>
    Info,

    /// <summary>Se lleva con redondeo o aproximación.</summary>
    Approximate,

    /// <summary>No se lleva.</summary>
    Lost,
}

public enum IssueKind
{
    Color,
    Transform,
    Transition,
    Speed,
    Title,
    Compound,
    Audio,
    Marker,
    Rounding,
    Media,
    Effect,
    Track,
    Unknown,
}

/// <summary>Una cosa que no viajó igual; <see cref="Time"/> está en tiempo de la secuencia.</summary>
public sealed record InterchangeIssue(IssueSeverity Severity, IssueKind Kind, string? ClipId, string Name, Time? Time, string Message);

/// <summary>Lo que se perdió o se aproximó al exportar o importar una línea de tiempo.</summary>
public sealed class InterchangeReport
{
    private readonly List<InterchangeIssue> _issues = [];

    public IReadOnlyList<InterchangeIssue> Issues => _issues;

    public bool IsEmpty => _issues.Count == 0;

    public void Add(InterchangeIssue issue) => _issues.Add(issue);

    public void Add(IssueSeverity severity, IssueKind kind, string message, string name = "", string? clipId = null, Time? time = null) =>
        _issues.Add(new InterchangeIssue(severity, kind, clipId, name, time, message));

    public void AddRange(InterchangeReport other) => _issues.AddRange(other._issues);

    /// <summary>Una línea por mensaje distinto, la más grave primero: «12 clips: se pierde el color».</summary>
    public IReadOnlyList<string> Summary() =>
        _issues
            .GroupBy(i => (i.Severity, i.Kind, i.Message))
            .OrderByDescending(g => g.Key.Severity)
            .ThenBy(g => g.Key.Kind)
            .Select(g => g.Count() == 1 ? $"{SeverityText(g.Key.Severity)}: {g.Key.Message}" : $"{SeverityText(g.Key.Severity)} ({g.Count()}): {g.Key.Message}")
            .ToList();

    /// <summary>La lista completa, una línea por aviso con su instante (<paramref name="formatTime"/> lo escribe como TC de la secuencia); para «Copiar» y «Guardar como .txt».</summary>
    public string ToText(Func<Time, string> formatTime)
    {
        var text = new StringBuilder();
        foreach (InterchangeIssue issue in _issues.OrderBy(i => i.Time?.Ticks ?? long.MinValue))
        {
            string when = issue.Time is { } t ? formatTime(t) + "  " : "";
            string name = issue.Name.Length > 0 ? $"«{issue.Name}» " : "";
            text.Append(CultureInfo.InvariantCulture, $"{when}{SeverityText(issue.Severity)}: {name}{issue.Message}").AppendLine();
        }

        return text.ToString();
    }

    public static string SeverityText(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Info => "Info",
        IssueSeverity.Approximate => "Aproximado",
        _ => "Perdido",
    };
}
