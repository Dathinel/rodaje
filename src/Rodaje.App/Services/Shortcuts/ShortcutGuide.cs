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
        IEnumerable<(string? Heading, IReadOnlyList<CheatSheetEntry> Entries)> sections = Groups(registry.Actions)
            .Select(group => ((string?)group, Entries(registry, group)))
            .Where(section => section.Item2.Count > 0);
        return CheatSheet.ToHtml("Atajos de Rodaje", subtitle, sections);
    }

    private static IReadOnlyList<CheatSheetEntry> Entries(ShortcutRegistry registry, string group) =>
    [
        .. registry.Actions
            .Where(a => a.Group == group && registry.GetKeys(a.Id).Count > 0)
            .Select(a => new CheatSheetEntry(a.Id, a.Label, a.Group, [.. registry.GetKeys(a.Id).Select(k => k.ToString())])),
    ];

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
}
