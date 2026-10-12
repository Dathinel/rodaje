using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>ProjectFileTests.Save_RelativePath_IsResolvedAndWorks</c>: allí solo se miraba que existiera
/// el archivo, así que un guardado que escribiera un proyecto vacío, truncado o con el temporal olvidado seguía en verde.
/// </summary>
public sealed class HardenedProjectFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Rodaje pruebas", $"Endurecidas ñ {Guid.NewGuid():N}");

    public HardenedProjectFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Una carpeta temporal sobrante no afecta a otras pruebas.
        }
        catch (UnauthorizedAccessException)
        {
            // Igual: el antivirus puede tener abierto un archivo recién escrito.
        }
    }

    [Fact]
    public void Saving_to_a_relative_path_writes_the_whole_project_where_it_resolves_and_leaves_nothing_else()
    {
        string absolute = Path.Combine(_dir, "relativo con tilde á" + ProjectSerializer.FileExtension);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, absolute);
        Assert.NotEqual(absolute, relative);
        Project project = SerializationSamples.Rich();

        ProjectSerializer.Save(project, relative);

        // Lo guardado es el proyecto entero, se abre igual por la ruta absoluta y por la relativa.
        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), ProjectSerializer.Load(absolute));
        SerializationSamples.AssertDeepEqual(SerializationSamples.Rich(), ProjectSerializer.Load(relative));
        Assert.Equal(ProjectSerializer.Serialize(SerializationSamples.Rich()) + "\n", File.ReadAllText(absolute));

        // En la carpeta queda solo el proyecto: ni el temporal ni copias con otro nombre.
        Assert.Equal([Path.GetFileName(absolute)], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }
}
