using System.Text;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

/// <summary>
/// La guía <c>docs/guia/atajos.md</c> se genera desde el catálogo y tiene que estar al día. Si cambia un atajo de fábrica,
/// se regenera con <c>RODAJE_ESCRIBIR_ATAJOS=1</c> (la prueba escribe el archivo en lugar de compararlo).
/// </summary>
public class ShortcutGuideTests
{
    private const string WriteVariable = "RODAJE_ESCRIBIR_ATAJOS";

    [Fact]
    public void LaGuiaDelRepositorioEsLaGenerada()
    {
        string expected = ShortcutGuide.ToMarkdown();
        string path = Path.Combine(RepositoryRoot(), ShortcutGuide.GuidePath.Replace('/', Path.DirectorySeparatorChar));
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, expected, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.True(File.Exists(path), $"No está {path}.");
        string actual = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(expected == actual, $"{ShortcutGuide.GuidePath} no coincide con el catálogo: regenérala con {WriteVariable}=1.");
    }

    [Fact]
    public void ElCatalogoNoRepiteIdentificadoresNiTextos()
    {
        IReadOnlyList<ShortcutAction> all = ShortcutCatalog.All;

        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(a => a.Group + "/" + a.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, a => Assert.Contains(a.Group, ShortcutGroups.Order));
    }

    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void NingunEstiloTieneDosAccionesConLaMismaTecla(ShortcutPreset preset)
    {
        ShortcutRegistry registry = ShortcutCatalog.CreateRegistry(preset);

        IReadOnlyList<ShortcutConflict> conflicts = registry.FindConflicts();

        Assert.True(conflicts.Count == 0, string.Join("; ", conflicts.Select(c => $"{c.Chord}: {string.Join(", ", c.ActionIds)}")));
    }

    [Fact]
    public void LaGuiaTieneTodasLasAccionesConTeclaYLosTresEstilos()
    {
        string markdown = ShortcutGuide.ToMarkdown();

        Assert.StartsWith("# Atajos de teclado y ratón", markdown, StringComparison.Ordinal);
        Assert.Contains("| Acción | Rodaje | Ágil | Clásico |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cortar en el cursor | S, Ctrl+B | Ctrl+B | S |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Ir a tiempo… | Ctrl+J | = | = |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Duplicar clip | Ctrl+D | = | Ctrl+Mayús+D |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Añadir región | R | (sin tecla) | = |", markdown, StringComparison.Ordinal);
        Assert.Contains("Sin tecla de fábrica (se asignan en el panel): ", markdown, StringComparison.Ordinal);
        Assert.Contains("| Ctrl+Rueda | Zoom de la línea de tiempo | = | Desplazar en el tiempo |", markdown, StringComparison.Ordinal);
        foreach (ShortcutAction action in ShortcutCatalog.All)
        {
            Assert.Contains(action.Label, markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LaHojaImprimibleListaSoloLasAccionesConTeclaPorGrupo()
    {
        ShortcutRegistry registry = ShortcutCatalog.CreateRegistry(ShortcutPreset.Classic);
        registry.RemoveKey(ActionIds.Split, KeyChord.Parse("S"));

        string html = ShortcutGuide.ToHtml(registry, "Estilo Clásico");

        Assert.Contains("<h2>Reproducción</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>Ctrl+M</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("Estilo Clásico", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Cortar en el cursor", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Carpetas vigiladas", html, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Rodaje.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (Rodaje.slnx).");
    }
}
