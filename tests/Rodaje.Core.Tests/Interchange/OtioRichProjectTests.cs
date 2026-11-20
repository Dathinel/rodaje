using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>OTIO con un proyecto con de todo (el de las pruebas de mutación), no solo con secuencias generadas.</summary>
public sealed class OtioRichProjectTests
{
    [Fact]
    public void A_rich_project_comes_back_whole_through_otio()
    {
        Project project = ProjectSerializer.Deserialize(Fuzz.ProjectFileMutationFuzzTests.RichProject());
        Sequence sequence = project.Sequences[0];
        var options = new InterchangeOptions { FrameRate = sequence.FrameRate };

        TimelineDraft expected = Fidelity.Project(project, sequence, InterchangeFormat.Otio, options, new InterchangeReport());
        string text = InterchangeFiles.Serialize(project, sequence, null, InterchangeFormat.Otio, options, new InterchangeReport())[0].Text;
        var report = new InterchangeReport();
        TimelineDraft read = InterchangeFiles.ReadDraft(text, InterchangeFormat.Otio, null, options, report);

        Assert.True(Fidelity.Difference(expected, read) is null, Fidelity.Difference(expected, read) + "\n" + string.Join("\n", report.Issues.Select(i => i.Message)));
    }
}
