using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Tests.Analysis;

public class AnalysisApplyStateTests
{
    [Theory]
    [InlineData(AnalysisApplyPhase.NeedsAnalysis, true)]
    [InlineData(AnalysisApplyPhase.Analyzing, true)]
    [InlineData(AnalysisApplyPhase.Ready, true)]
    [InlineData(AnalysisApplyPhase.Blocked, false)]
    public void Boton_principal_solo_se_bloquea_si_falta_un_requisito(AnalysisApplyPhase phase, bool expected) =>
        Assert.Equal(expected, AnalysisApplyState.IsEnabled(phase));
}
