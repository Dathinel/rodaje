using System.Text;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// La guía de atajos (<c>docs/guia/atajos.md</c>) y la hoja imprimible de Ayuda, generadas desde el catálogo
/// (<see cref="ShortcutCatalog.All"/>) y el ratón de fábrica (<see cref="MouseDefaults"/>), así nunca se desincronizan de
/// la app. <c>ShortcutGuideTests</c> comprueba que la guía del repositorio sea exactamente <see cref="ToMarkdown"/>.
/// </summary>
public static class ShortcutGuide
{
    /// <summary>Dónde vive la guía en el repositorio, relativo a su raíz.</summary>
    public const string GuidePath = "docs/guia/atajos.md";

    private const string Same = "=";
    private const string NoKey = "(sin tecla)";
    private const string NoAction = "(sin acción)";

    private static readonly (ShortcutPreset Preset, string Name)[] Presets =
    [
        (ShortcutPreset.Rodaje, "Rodaje"),
        (ShortcutPreset.Agile, "Ágil"),
        (ShortcutPreset.Classic, "Clásico"),
    ];

    /// <summary>La guía entera en Markdown, con saltos de línea LF.</summary>
    public static string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("# Atajos de teclado y ratón\n\n");
        md.Append("<!-- Generado desde el catálogo de atajos de la app (ShortcutGuide.ToMarkdown); no se edita a mano. ");
        md.Append("Para regenerarlo: RODAJE_ESCRIBIR_ATAJOS=1 y la prueba ShortcutGuideTests. -->\n\n");
        md.Append("Rodaje trae tres estilos de teclado y todo se puede cambiar. El estilo se elige en **Archivo › Preferencias...** (pestaña **Teclado**) o en el propio editor de atajos, y el panel se abre con **Archivo › Atajos de teclado...** o **Ctrl+Alt+K**.\n\n");
        md.Append("- **Rodaje**: el de fábrica. Letras sueltas para lo frecuente (S corta, Q y W recortan, M marcador, N ajuste).\n");
        md.Append("- **Ágil**: cercano a los editores rápidos de escritorio (Ctrl+B corta, A herramienta normal, Ctrl+G compuesto).\n");
        md.Append("- **Clásico**: cercano a los editores profesionales (S corta, Ctrl+M exporta, flechas con Alt para fotogramas, ↑ y ↓ hacen zoom).\n\n");
        md.Append("En las tablas, «=» significa «igual que Rodaje» y «(sin tecla)», que ese estilo no trae tecla de fábrica. Cada menú muestra la tecla vigente junto a su acción, también los menús del clip y de la cabecera de pista.\n");

        foreach (string group in Groups())
        {
            md.Append("\n## ").Append(group).Append("\n\n");
            md.Append("| Acción | Rodaje | Ágil | Clásico |\n|---|---|---|---|\n");
            var unbound = new List<string>();
            foreach (ShortcutAction action in ShortcutCatalog.All.Where(a => a.Group == group))
            {
                if (Presets.All(p => action.DefaultKeys(p.Preset).Count == 0))
                {
                    unbound.Add(action.Label);
                    continue;
                }

                md.Append("| ").Append(action.Label);
                IReadOnlyList<KeyChord> rodaje = action.DefaultKeys(ShortcutPreset.Rodaje);
                foreach ((ShortcutPreset preset, _) in Presets)
                {
                    IReadOnlyList<KeyChord> keys = action.DefaultKeys(preset);
                    md.Append(" | ").Append(preset != ShortcutPreset.Rodaje && keys.SequenceEqual(rodaje) ? Same : Keys(keys));
                }

                md.Append(" |\n");
            }

            if (unbound.Count > 0)
            {
                md.Append("\nSin tecla de fábrica (se asignan en el panel): ").Append(string.Join(", ", unbound)).Append(".\n");
            }
        }

        md.Append("\n## Ratón\n\n");
        md.Append("Clic izquierdo y derecho son fijos: seleccionar y arrastrar (Ctrl suma a la selección, Alt arrastra sin ajuste) y menú contextual. El resto se cambia por zona en la pestaña Ratón del panel de atajos.\n");
        foreach (MouseContext context in Enum.GetValues<MouseContext>())
        {
            md.Append("\n**").Append(MouseNames.Display(context)).Append("**\n\n");
            md.Append("| Parte | Rodaje | Ágil | Clásico |\n|---|---|---|---|\n");
            IEnumerable<MouseChord> chords = Presets
                .SelectMany(p => MouseDefaults.For(p.Preset, context).Keys)
                .Distinct()
                .OrderBy(c => c.Input).ThenBy(c => (int)c.Modifiers);
            foreach (MouseChord chord in chords)
            {
                md.Append("| ").Append(chord.ToString());
                string rodaje = MouseLabel(ShortcutPreset.Rodaje, context, chord);
                foreach ((ShortcutPreset preset, _) in Presets)
                {
                    string label = MouseLabel(preset, context, chord);
                    md.Append(" | ").Append(preset != ShortcutPreset.Rodaje && label == rodaje ? Same : label);
                }

                md.Append(" |\n");
            }
        }

        md.Append("\n## Cambiar los atajos\n\n");
        md.Append("En **Archivo › Atajos de teclado...** (Ctrl+Alt+K) cada acción muestra sus teclas: un clic en una y se pulsa la nueva combinación; si ya la usaba otra acción, se avisa y se puede intercambiar. Los cambios se guardan por estilo en `atajos.json` y «Restablecer» vuelve a los de fábrica. La pestaña **Ratón** hace lo mismo con los botones y la rueda de cada zona.\n\n");
        md.Append("**Exportar… / Importar…** guardan y leen un JSON con los tres estilos, para pasarlos a otro equipo.\n\n");
        md.Append("## Hojas para imprimir\n\n");
        md.Append("En el panel, **Hoja rápida** junta los 15 atajos más útiles del estilo actual y los guarda en Documentos como «Atajos de Rodaje.html» (imprimible) o «Atajos de Rodaje.png». **Ayuda › Hoja de atajos (imprimible)...** guarda todos los atajos del estilo actual, con los cambios de cada uno, como «Atajos de Rodaje (completa).html».\n");
        return md.ToString();
    }

    /// <summary>
    /// Todos los atajos con tecla del registro (estilo y cambios actuales), por grupo, en una página HTML sola lista
    /// para imprimir. Las acciones sin tecla no salen.
    /// </summary>
    public static string ToHtml(ShortcutRegistry registry, string subtitle)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="es">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Atajos de Rodaje</title>
            <style>
              :root { --bg: #ffffff; --text: #1b1b1b; --muted: #5e5e5e; --line: #dddddd; --key: #f3f3f3; --accent: #c96d29; }
              @media (prefers-color-scheme: dark) { :root { --bg: #141414; --text: #d8d8d8; --muted: #9a9a9a; --line: #2e2e2e; --key: #232323; --accent: #e8833a; } }
              @media print { :root { --bg: #ffffff; --text: #000000; --muted: #444444; --line: #bbbbbb; --key: #f3f3f3; --accent: #000000; } }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 24px 16px; background: var(--bg); color: var(--text); font: 13px/1.4 "Segoe UI", system-ui, sans-serif; }
              main { max-width: 900px; margin: 0 auto; }
              h1 { margin: 0; font-size: 20px; }
              h2 { margin: 20px 0 6px; font-size: 14px; color: var(--accent); break-after: avoid; }
              p.sub { margin: 4px 0 8px; color: var(--muted); }
              ol { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 0 24px; }
              li { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 5px 0; border-bottom: 1px solid var(--line); break-inside: avoid; }
              .keys { display: flex; gap: 4px; flex-wrap: wrap; justify-content: flex-end; }
              kbd { font: 600 11px/1 "Segoe UI", system-ui, sans-serif; padding: 3px 6px; border: 1px solid var(--line); border-bottom-width: 2px; border-radius: 3px; background: var(--key); white-space: nowrap; }
              @page { margin: 12mm; }
            </style>
            </head>
            <body>
            <main>
            <h1>Atajos de Rodaje</h1>

            """);
        html.Append("<p class=\"sub\">").Append(Encode(subtitle)).Append("</p>\n");
        foreach (string group in Groups(registry.Actions))
        {
            List<ShortcutAction> actions = [.. registry.Actions.Where(a => a.Group == group && registry.GetKeys(a.Id).Count > 0)];
            if (actions.Count == 0)
            {
                continue;
            }

            html.Append("<h2>").Append(Encode(group)).Append("</h2>\n<ol>\n");
            foreach (ShortcutAction action in actions)
            {
                html.Append("  <li><span>").Append(Encode(action.Label)).Append("</span><span class=\"keys\">");
                html.Append(string.Join(' ', registry.GetKeys(action.Id).Select(k => "<kbd>" + Encode(k.ToString()) + "</kbd>")));
                html.Append("</span></li>\n");
            }

            html.Append("</ol>\n");
        }

        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static IEnumerable<string> Groups(IEnumerable<ShortcutAction>? actions = null)
    {
        IEnumerable<string> present = (actions ?? ShortcutCatalog.All).Select(a => a.Group).Distinct();
        return ShortcutGroups.Order.Where(present.Contains).Concat(present.Where(g => !ShortcutGroups.Order.Contains(g)));
    }

    private static string Keys(IReadOnlyList<KeyChord> keys) =>
        keys.Count == 0 ? NoKey : string.Join(", ", keys.Select(k => k.ToString()));

    private static string MouseLabel(ShortcutPreset preset, MouseContext context, MouseChord chord)
    {
        string? id = MouseDefaults.For(preset, context).GetValueOrDefault(chord);
        if (id is null)
        {
            return NoAction;
        }

        return MouseGestures.Find(id)?.Label ?? ShortcutCatalog.All.FirstOrDefault(a => a.Id == id)?.Label ?? id;
    }

    private static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
