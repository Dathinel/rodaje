using System.Text;
using System.Text.Json;
using Rodaje.Module.Translate.Translation;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Translate.Tests;

/// <summary>
/// El módulo carga el modelo aunque su ruta tenga «ñ» o tildes (una carpeta de usuario «José», o la de las pruebas):
/// LLamaSharp pasa la ruta como texto ANSI y el manifiesto del módulo pone la página de códigos en UTF-8.
/// </summary>
[Collection("Qwen")]
[Trait("Categoria", "SoloWindows")]
public sealed class AccentedPathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetDirectoryName(ModelFactAttribute.ModelPath ?? Path.GetTempPath())!, "prueba ñandú " + Guid.NewGuid().ToString("N")[..6]);

    [ModelFact]
    public async Task The_module_loads_the_model_from_a_folder_with_accents()
    {
        Directory.CreateDirectory(_folder);
        string link = Path.Combine(_folder, "modelo ñ.gguf");
        if (!CreateHardLink(link, ModelFactAttribute.ModelPath!, IntPtr.Zero))
        {
            File.Copy(ModelFactAttribute.ModelPath!, link);
        }

        string exe = Environment.GetEnvironmentVariable("RODAJE_MODULO_TRADUCIR") is { Length: > 0 } e ? e : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Translate.exe");
        await using ModuleProcess module = await ModuleProcess.StartAsync(exe);
        byte[] input = Encoding.UTF8.GetBytes("""{"segments":[{"id":"c1","text":"Hola a todos."}]}""");
        SharedMemoryRegion inRegion = module.Client.CreateRegion(input.Length);
        input.CopyTo(inRegion.GetSpan(0, input.Length));
        SharedMemoryRegion outRegion = module.Client.CreateRegion(1 << 16);
        ModuleResult result = await module.Client.InvokeAsync("traducir",
            RequestTests.Json($$"""{"source":"es","target":"en","device":"cpu","model":{{JsonSerializer.Serialize(link)}}}"""),
            [inRegion.Describe(0, input.Length), outRegion.Describe(0, 1 << 16)]);
        int bytes = result.Value!.Value.GetProperty("bytes").GetInt32();
        TranslationResult r = JsonSerializer.Deserialize(outRegion.GetSpan(0, bytes), TranslationJson.Default.TranslationResult)!;
        Assert.Contains("everyone", r.Segments[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
