using System.Text.Json;
using Rodaje.Media.Gpu;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Rodaje.Text.Tests;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Text;

/// <summary>
/// La maquetación de DirectWrite en los casos de aceptación de títulos (D-066, fase 5): cada unidad con su línea, palabra y
/// rectángulo, leída del rasterizador de verdad. Es la referencia que la maquetación de HarfBuzz tiene que repetir en Linux y
/// macOS. Si falta el archivo de un caso se escribe en TestResults/maquetacion para copiarlo a Rodaje.Text.Tests/Maquetacion.
/// </summary>
public sealed class TitleLayoutReferenceTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases() => [.. TitleLayoutCases.All.Select(c => c.Name)];

    [GpuTheory]
    [MemberData(nameof(Cases))]
    public void DirectWrite_lays_out_the_case_as_its_reference_says(string name)
    {
        TitleLayoutCase @case = TitleLayoutCases.All.Single(c => c.Name == name);
        string root = TitleLayoutCases.RepositoryRoot();
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        Assert.Empty(titles.RegisterFonts(TitleLayoutCases.FontFiles.Select(f => Path.Combine(root, f))));

        TitleTexture texture = titles.Rasterize(TitleImage.Content(@case.Text, @case.Style(), @case.BoxWidth))!;
        TitleUnitSet set = texture.Units!;
        LaidOutTitle actual = LaidOutTitle.Rounded(name, "DirectWrite", set.Units.Select(u =>
            new LaidOutUnit(u.Start, u.End, u.Line, u.Word, u.InkLeft, u.InkRight, u.Top, u.Bottom)));
        string json = JsonSerializer.Serialize(actual, TitleLayoutCases.Json);

        string golden = TitleLayoutCases.GoldenPath(root, name);
        if (!File.Exists(golden))
        {
            string written = Path.Combine(root, "TestResults", "maquetacion", name + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(written)!);
            File.WriteAllText(written, json);
            output.WriteLine($"Sin referencia: escrita en {written}.");
            return;
        }

        LaidOutTitle expected = JsonSerializer.Deserialize<LaidOutTitle>(File.ReadAllText(golden), TitleLayoutCases.Json)!;
        Assert.Equal(expected.Units.Count, actual.Units.Count);
        for (int i = 0; i < expected.Units.Count; i++)
        {
            Assert.Equal(expected.Units[i], actual.Units[i]);
        }
    }
}
