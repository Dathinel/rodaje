using System.Text;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una línea de la hoja rápida.</summary>
public sealed record CheatSheetEntry(string ActionId, string Label, string Group, IReadOnlyList<string> Keys);

/// <summary>
/// La hoja rápida: los atajos más útiles del estilo actual, para tenerlos a mano o imprimirlos. Sale en la página de
/// atajos y se exporta como HTML (imprimible) o PNG.
/// </summary>
public static class CheatSheet
{
    public const int DefaultCount = 15;

    /// <summary>Las acciones por orden de utilidad para alguien que empieza a editar.</summary>
    public static IReadOnlyList<string> Priority { get; } =
    [
        ActionIds.TogglePlay,
        ActionIds.Split,
        ActionIds.Undo,
        ActionIds.Redo,
        ActionIds.Delete,
        ActionIds.PreviousFrame,
        ActionIds.NextFrame,
        ActionIds.TrimStart,
        ActionIds.TrimEnd,
        ActionIds.ClipSpeed,
        ActionIds.FreezeFrame,
        ActionIds.Save,
        ActionIds.Import,
        ActionIds.ZoomIn,
        ActionIds.ZoomOut,
        ActionIds.AddMarker,
        ActionIds.GoToStart,
        ActionIds.GoToEnd,
        ActionIds.SelectAll,
        ActionIds.ToggleSnapping,
        ActionIds.Export,
        ActionIds.NormalTool,
    ];

    /// <summary>
    /// Hasta <paramref name="count"/> acciones con tecla: primero las de <see cref="Priority"/> y, si faltan, las demás
    /// en el orden del registro. Cada una lleva como mucho dos teclas.
    /// </summary>
    public static IReadOnlyList<CheatSheetEntry> Select(ShortcutRegistry registry, Func<KeyChord, string>? keyText = null, int count = DefaultCount)
    {
        ArgumentNullException.ThrowIfNull(registry);
        keyText ??= KeyNames.Format;
        var entries = new List<CheatSheetEntry>();
        IEnumerable<ShortcutAction> candidates = Priority
            .Select(registry.Find)
            .OfType<ShortcutAction>()
            .Concat(registry.Actions.Where(a => !Priority.Contains(a.Id)));
        foreach (ShortcutAction action in candidates)
        {
            if (entries.Count >= count)
            {
                break;
            }

            IReadOnlyList<KeyChord> keys = registry.GetKeys(action.Id);
            if (keys.Count > 0)
            {
                entries.Add(new CheatSheetEntry(action.Id, action.Label, action.Group, [.. keys.Take(2).Select(keyText)]));
            }
        }

        return entries;
    }

    /// <summary>Una página HTML sola (sin archivos aparte), clara en pantalla y lista para imprimir en una hoja.</summary>
    public static string ToHtml(IReadOnlyList<CheatSheetEntry> entries, string title, string subtitle)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="es">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        html.Append("<title>").Append(Encode(title)).Append("</title>\n");
        html.Append("""
            <style>
              :root { --bg: #ffffff; --text: #1b1b1b; --muted: #5e5e5e; --line: #dddddd; --key: #f3f3f3; --accent: #c96d29; }
              @media (prefers-color-scheme: dark) { :root { --bg: #141414; --text: #d8d8d8; --muted: #9a9a9a; --line: #2e2e2e; --key: #232323; --accent: #e8833a; } }
              @media print { :root { --bg: #ffffff; --text: #000000; --muted: #444444; --line: #bbbbbb; --key: #f3f3f3; --accent: #000000; } }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 24px 16px; background: var(--bg); color: var(--text); font: 14px/1.4 "Segoe UI", system-ui, sans-serif; }
              main { max-width: 860px; margin: 0 auto; }
              h1 { margin: 0; font-size: 20px; }
              p.sub { margin: 4px 0 20px; color: var(--muted); }
              ol { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(250px, 1fr)); gap: 0 24px; }
              li { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 8px 0; border-bottom: 1px solid var(--line); break-inside: avoid; }
              .keys { display: flex; gap: 4px; flex-wrap: wrap; justify-content: flex-end; }
              kbd { font: 600 12px/1 "Segoe UI", system-ui, sans-serif; padding: 4px 7px; border: 1px solid var(--line); border-bottom-width: 2px; border-radius: 3px; background: var(--key); white-space: nowrap; }
              @page { margin: 14mm; }
            </style>
            </head>
            <body>
            <main>
            """);
        html.Append("<h1>").Append(Encode(title)).Append("</h1>\n");
        html.Append("<p class=\"sub\">").Append(Encode(subtitle)).Append("</p>\n<ol>\n");
        foreach (CheatSheetEntry entry in entries)
        {
            html.Append("  <li><span>").Append(Encode(entry.Label)).Append("</span><span class=\"keys\">");
            for (int i = 0; i < entry.Keys.Count; i++)
            {
                if (i > 0)
                {
                    html.Append(' ');
                }

                html.Append("<kbd>").Append(Encode(entry.Keys[i])).Append("</kbd>");
            }

            html.Append("</span></li>\n");
        }

        html.Append("</ol>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Escapa solo lo necesario: las tildes y la eñe quedan legibles en el archivo (va en UTF-8).</summary>
    private static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}
