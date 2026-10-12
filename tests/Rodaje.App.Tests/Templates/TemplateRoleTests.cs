using Rodaje.App.Services.Templates;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Templates;

public sealed class TemplateRoleTests
{
    [Theory]
    [InlineData("Música!")]
    [InlineData("subtitles")]
    public void Un_rol_invalido_de_una_plantilla_se_descarta_y_el_proyecto_es_valido(string role)
    {
        var t = new ProjectTemplate
        {
            Id = "propia.x",
            Name = "X",
            VideoTracks = [new TemplateTrack { Name = "V" }],
            AudioTracks = [new TemplateTrack { Name = "A", Role = role }],
        };
        Project p = t.CreateProject("p", new SequentialIdGenerator());
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }
}
