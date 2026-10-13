using System.Text;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Packages.Tests.Fuzz;
using Rodaje.Tests.Fuzz;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// El mismo fuzz de mutaciones de la tarea 94 sobre los <c>.rodaje-preajuste</c> que importa el diálogo de exportar
/// (<see cref="ExportPresetLibrary.Import"/>): bytes cambiados o truncados, tipos cambiados, números y textos enormes,
/// enumeraciones que no existen. La importación solo puede fallar con lo que la vista avisa
/// (<see cref="InvalidDataException"/>, <see cref="InvalidOperationException"/>, E/S), con mensaje; si importa, el
/// diálogo puede elegir el preajuste, resumirlo, estimarlo y validarlo sin lanzar (tarea 102).
/// </summary>
public sealed class ExportPresetFileFuzzTests : IDisposable
{
    private const int Seed = 102_100_000;

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Preajustes que cubren video y audio, solo audio, GIF y un maestro con perfil y profundidad.</summary>
    private static readonly ExportPreset[] Sources =
        [ExportPresets.YouTube1080p, ExportPresets.ProResMaster, ExportPresets.Gif, ExportPresets.AudioOnly, ExportPresets.SmallFile];

    private byte[] SeedFile(ExportPreset preset)
    {
        string path = _dir.File("semilla" + ExportPresetLibrary.FileExtension);
        ExportPresetLibrary.WriteFile(preset, path);
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Seeds_AreImported()
    {
        foreach (ExportPreset preset in Sources)
        {
            Assert.Null(Attempt(SeedFile(preset), new ExportPreferences(), preset.Name));
        }
    }

    [Fact]
    public void MutatedPresets_OnlyThrowWhatTheDialogReports()
    {
        byte[][] seeds = [.. Sources.Select(SeedFile)];
        FuzzRunner.Run("rodaje-preajuste", Seed, cases: 1500, TimeSpan.FromSeconds(20), minimumCases: 300, (seed, rng) =>
        {
            byte[] data = Mutate(seeds[rng.Next(seeds.Length)], rng);
            _ = Attempt(data, new ExportPreferences(), $"semilla {seed}");
        });
    }

    /// <summary>
    /// Un <c>null</c> donde va un objeto o un número que no es de ninguna opción se importaba sin quejarse y el diálogo
    /// lanzaba <see cref="NullReferenceException"/> al elegir el preajuste (defecto hallado por este fuzz).
    /// </summary>
    [Theory]
    [InlineData("\"rateControl\": {", "\"rateControl\": null, \"x\": {", 0)]
    [InlineData("\"rateControl\": {", "\"rateControl\": null, \"x\": {", 1)]
    [InlineData("\"gop\": {", "\"gop\": null, \"x\": {", 0)]
    [InlineData("\"container\": \"Mp4\"", "\"container\": 99", 0)]
    [InlineData("\"codec\": \"H264\"", "\"codec\": 999", 0)]
    [InlineData("\"codec\": \"Aac\"", "\"codec\": -1", 0)]
    [InlineData("\"chroma\": \"Chroma420\"", "\"chroma\": 7", 0)]
    [InlineData("\"mode\": \"Vbr\"", "\"mode\": 42", 0)]
    public void DamagedSettings_AreRejectedWithAClearMessage(string from, string to, int occurrence)
    {
        string text = Encoding.UTF8.GetString(SeedFile(ExportPresets.YouTube1080p));
        int at = -1;
        for (int k = 0; k <= occurrence; k++)
        {
            at = text.IndexOf(from, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"La semilla no tiene «{from}» número {occurrence}.");
        }

        string damaged = text[..at] + to + text[(at + from.Length)..];
        // «x» se lleva el objeto que había para que el JSON siga bien formado (y la propiedad desconocida se ignora).
        string? message = Attempt(Encoding.UTF8.GetBytes(damaged), new ExportPreferences(), from);
        Assert.NotNull(message);
        Assert.Contains("dañado", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dos mil millones de pasadas y un cuadro de un millón de píxeles de lado: la estimación de tiempo del diálogo lanzaba <see cref="OverflowException"/> en vez de
    /// dejar que el diálogo lo avise como problema (defecto hallado por este fuzz).
    /// </summary>
    [Fact]
    public void AbsurdPasses_AreReportedNotThrown()
    {
        string text = Encoding.UTF8.GetString(SeedFile(ExportPresets.YouTube1080p))
            .Replace("\"passes\": 1", "\"passes\": 2147483647", StringComparison.Ordinal)
            .Replace("\"width\": 1920", "\"width\": 1000000", StringComparison.Ordinal)
            .Replace("\"height\": 1080", "\"height\": 1000000", StringComparison.Ordinal);
        Assert.Contains("2147483647", text, StringComparison.Ordinal);
        Assert.Contains("\"height\": 1000000", text, StringComparison.Ordinal);
        var preferences = new ExportPreferences();
        Assert.Null(Attempt(Encoding.UTF8.GetBytes(text), preferences, "pasadas"));

        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(preferences, project.Sequences[0], [], SimulatedEncoders.All, project.Name, _dir.Path);
        model.SelectPreset(preferences.UserPresets[0].Id);
        Assert.Equal(TimeSpan.MaxValue, model.Estimate.Time);
        Assert.False(model.CanExport);
    }

    /// <summary>Cadenas de 10 MB, números enormes y listas de un millón de ceros en cada campo.</summary>
    [Fact]
    public void HugeValues_GiveClearErrors()
    {
        string huge = new('ñ', 10 * 1024 * 1024);
        string text = Encoding.UTF8.GetString(SeedFile(ExportPresets.ProResMaster));
        int fields = text.Split("\": ").Length - 1;
        foreach (string replacement in new[] { $"\"{huge}\"", "1e400", "-1e400", "99999999999999999999", "-2147483649",
            "[" + string.Join(',', Enumerable.Repeat("0", 1_000_000)) + "]" })
        {
            for (int occurrence = 0; occurrence < fields; occurrence++)
            {
                _ = Attempt(Encoding.UTF8.GetBytes(ReplaceValue(text, occurrence, replacement)), new ExportPreferences(), $"campo {occurrence}");
            }
        }
    }

    // --- Comprobación ----------------------------------------------------------------------------------------------------

    /// <summary>Importa y, si sale bien, lo usa como lo usa el diálogo. Devuelve el mensaje del error esperado, si lo hubo.</summary>
    private string? Attempt(byte[] data, ExportPreferences preferences, string what)
    {
        string path = _dir.File("caso" + ExportPresetLibrary.FileExtension);
        File.WriteAllBytes(path, data);
        ExportPreset preset;
        try
        {
            preset = ExportPresetLibrary.Import(preferences, path);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Assert.False(string.IsNullOrWhiteSpace(ex.Message), what);
            return ex.Message;
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{what}: excepción no controlada {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n{ContentReaderFuzz.Show(data)}");
        }

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name), what);
            Assert.Contains(preferences.UserPresets, p => p.Id == preset.Id);
            Assert.False(string.IsNullOrWhiteSpace(ExportPresetLibrary.Summary(preset.Settings)), what);

            Project project = ExportTestData.Project();
            var model = new ExportDialogModel(preferences, project.Sequences[0], [], SimulatedEncoders.All, project.Name, _dir.Path);
            model.SelectPreset(preset.Id);
            _ = model.Problems;
            _ = model.OutputSize;
            _ = ExportText.Duration(model.Estimate.Time);
            _ = model.PresetShapeNote;

            // Lo importado se guarda en ajustes.json y se vuelve a leer al arrancar.
            preferences.Normalize();
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{what}: lo importado rompe el diálogo con {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n{ContentReaderFuzz.Show(data)}");
        }

        return null;
    }

    // --- Mutaciones ------------------------------------------------------------------------------------------------------

    /// <summary>Las de <see cref="ContentReaderFuzz.Mutate"/> y, a veces, un valor de otro tipo en un campo.</summary>
    private static byte[] Mutate(byte[] original, Random rng)
    {
        if (rng.Next(3) == 0)
        {
            string text = Encoding.UTF8.GetString(original);
            string[] replacements = ["null", "true", "[]", "{}", "\"\"", "0", "-1", "1e400", "1.5", "\"x\"", "[null]", "{\"a\":1}", "999",
                "-2147483648", "2147483647", "\"0/0\"", "\"1/0\"", "\"-30/1\"", "\"9223372036854775807/1\"", "\"\\ud800\"", "\"a\\u0000b\"",
                "\"" + new string('a', 1024 << rng.Next(8)) + "\"", "99999999999999999999", "\"NaN\"", "NaN"];
            int fields = text.Split("\": ").Length - 1;
            return Encoding.UTF8.GetBytes(ReplaceValue(text, rng.Next(Math.Max(1, fields)), replacements[rng.Next(replacements.Length)]));
        }

        return ContentReaderFuzz.Mutate(rng, original);
    }

    /// <summary>Cambia el valor de la propiedad número <paramref name="occurrence"/> (contando «": »), si la hay.</summary>
    private static string ReplaceValue(string json, int occurrence, string replacement)
    {
        int at = -1;
        for (int k = 0; k <= occurrence; k++)
        {
            at = json.IndexOf("\": ", at + 1, StringComparison.Ordinal);
            if (at < 0)
            {
                return json;
            }
        }

        int start = at + 3;
        int end = start;
        int depth = 0;
        bool inString = false;
        for (; end < json.Length; end++)
        {
            char c = json[end];
            if (inString)
            {
                if (c == '\\')
                {
                    end++;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (depth == 0)
                    {
                        end++;
                        break;
                    }
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']' or ',' && depth == 0)
            {
                break;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    end++;
                    break;
                }
            }
        }

        return json[..start] + replacement + json[end..];
    }
}
