using Rodaje.App.Views.Voice;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;

namespace Rodaje.App.Tests.Voice;

/// <summary>
/// Al cerrar el diálogo, soltar el grabador puede lanzar (con el disco lleno, el volcado final del búfer vuelve a
/// fallar): <see cref="VoiceOverSteps.Close"/> no deja escapar el error, lo avisa si no había otro y borra el WAV igual.
/// </summary>
public sealed class VoiceOverCloseDisposeTests
{
    [Fact]
    public void Si_Finish_y_Dispose_fallan_se_avisa_el_primer_error_y_se_borra()
    {
        var finishFailure = new IOException("No queda espacio en el disco.");
        var deleted = new List<string>();

        VoiceOverClosed closed = VoiceOverSteps.Close(false, "toma.wav", () => throw finishFailure,
            () => throw new IOException("No queda espacio en el disco (al cerrar)."), deleted.Add);

        Assert.Equal(VoiceOverOutcome.ReportFailure, closed.Outcome);
        Assert.Null(closed.Take);
        Assert.Same(finishFailure, closed.Failure);
        Assert.Equal(["toma.wav"], deleted);
    }

    [Fact]
    public void Si_solo_Dispose_falla_la_toma_no_entra_y_se_avisa()
    {
        var take = new VoiceOverTake("toma.wav", 4800, 48_000, Time.Zero);
        var disposeFailure = new IOException("No queda espacio en el disco.");
        var deleted = new List<string>();

        VoiceOverClosed closed = VoiceOverSteps.Close(false, "toma.wav", () => take, () => throw disposeFailure,
            deleted.Add);

        Assert.Equal(VoiceOverOutcome.ReportFailure, closed.Outcome);
        Assert.Null(closed.Take);
        Assert.Same(disposeFailure, closed.Failure);
        Assert.Equal(["toma.wav"], deleted);
    }

    [Fact]
    public void Un_acceso_denegado_al_soltar_tambien_se_avisa_como_error_de_escritura()
    {
        var denied = new UnauthorizedAccessException("Acceso denegado.");
        var deleted = new List<string>();

        VoiceOverClosed closed = VoiceOverSteps.Close(false, "toma.wav", () => throw new IOException("lleno"),
            () => throw denied, deleted.Add);
        Assert.Equal(VoiceOverOutcome.ReportFailure, closed.Outcome);
        Assert.Equal(["toma.wav"], deleted);

        var take = new VoiceOverTake("otra.wav", 480, 48_000, Time.Zero);
        closed = VoiceOverSteps.Close(false, "otra.wav", () => take, () => throw denied, deleted.Add);
        Assert.Equal(VoiceOverOutcome.ReportFailure, closed.Outcome);
        Assert.Same(denied, closed.Failure!.InnerException);
        Assert.Equal(["toma.wav", "otra.wav"], deleted);
    }

    [Fact]
    public void Si_se_cancelo_y_Dispose_falla_se_descarta_sin_avisar_y_se_borra()
    {
        var deleted = new List<string>();

        VoiceOverClosed closed = VoiceOverSteps.Close(true, "toma.wav", () => throw new IOException("lleno"),
            () => throw new IOException("lleno al cerrar"), deleted.Add);

        Assert.Equal(VoiceOverOutcome.Discard, closed.Outcome);
        Assert.Equal(["toma.wav"], deleted);
    }
}
