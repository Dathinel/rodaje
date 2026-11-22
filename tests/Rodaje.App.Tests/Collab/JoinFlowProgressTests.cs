using Rodaje.App.Services.Collab;
using Rodaje.App.Services.Operations;

namespace Rodaje.App.Tests.Collab;

public sealed class JoinFlowProgressTests
{
    [Fact]
    public void El_camino_tiene_entrar_los_pasos_del_reparto_y_buscar_los_medios()
    {
        Assert.Equal(1 + ProjectShareGuest.Steps + 1, JoinFlowProgress.Steps);
        Assert.Equal(JoinFlowProgress.Steps, JoinFlowProgress.SearchStep);

        var operation = new OperationProgress();
        JoinFlowProgress.Begin(operation);
        OperationSnapshot s = operation.Snapshot();
        Assert.Equal(1, s.Phase);
        Assert.Equal(JoinFlowProgress.Steps, s.PhaseCount);
        Assert.Contains("Conectando", s.PhaseText, StringComparison.Ordinal);
        Assert.Null(s.Fraction);

        JoinFlowProgress.Searching(operation);
        s = operation.Snapshot();
        Assert.Equal(JoinFlowProgress.Steps, s.Phase);
        Assert.Contains("Buscando", s.PhaseText, StringComparison.Ordinal);
    }

    [Fact]
    public void El_reparto_del_proyecto_cuenta_como_las_fases_2_a_4_y_la_fraccion_no_retrocede()
    {
        var operation = new OperationProgress();
        IProgress<ShareProgress> receive = JoinFlowProgress.Receive(operation);

        receive.Report(new ShareProgress(1, ProjectShareGuest.Steps, "Pidiendo el proyecto a quien hospeda la sala…", null));
        OperationSnapshot s = operation.Snapshot();
        Assert.Equal(2, s.Phase);
        Assert.Equal(JoinFlowProgress.Steps, s.PhaseCount);
        Assert.Null(s.Fraction);

        receive.Report(new ShareProgress(2, ProjectShareGuest.Steps, "Quien hospeda la sala está preparando tus archivos…", 0.5));
        receive.Report(new ShareProgress(3, ProjectShareGuest.Steps, "Recibiendo el proyecto (10 %)…", 0.1));
        s = operation.Snapshot();

        Assert.Equal(4, s.Phase);
        Assert.Equal("Recibiendo el proyecto (10 %)…", s.PhaseText);
        Assert.Equal(OperationProgress.PhaseFraction(4, JoinFlowProgress.Steps, 0.1), s.Fraction);
        Assert.True(s.Fraction > 0.5, "la fracción del camino no puede bajar al cambiar de fase");
    }

    [Fact]
    public void Un_avance_sin_porcentaje_deja_la_fase_indeterminada()
    {
        var operation = new OperationProgress();
        IProgress<ShareProgress> receive = JoinFlowProgress.Receive(operation);

        receive.Report(new ShareProgress(1, ProjectShareGuest.Steps, "Pidiendo el proyecto a quien hospeda la sala…", null));

        Assert.Null(operation.Snapshot().Fraction);
    }
}
