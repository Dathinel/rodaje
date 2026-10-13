using Rodaje.App.Views.Voice;
using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;

namespace Rodaje.App.Tests.Voice;

/// <summary>
/// El diálogo de la voz en off sin WinUI: el temporizador para en cuanto el grabador falla y, al cerrar, el WAV que no
/// entra en la línea de tiempo se borra, también si <c>Finish</c> falló (disco lleno) y no hay toma.
/// </summary>
public sealed class VoiceOverStepsTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-voz-pasos-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void La_cuenta_atras_cuenta_un_segundo_cada_diez_ticks_y_empieza_a_grabar_en_cero()
    {
        VoiceOverTickState state = VoiceOverTickState.Countdown(3);
        var shown = new List<int>();
        var actions = new List<VoiceOverTickAction>();
        for (int i = 0; i < 30; i++)
        {
            (state, VoiceOverTickAction action) = VoiceOverSteps.Tick(state, failed: false);
            actions.Add(action);
            if (action == VoiceOverTickAction.Countdown)
            {
                shown.Add(state.Remaining);
            }
        }

        Assert.Equal([2, 1], shown);
        Assert.Equal(27, actions.Count(a => a == VoiceOverTickAction.Wait));
        Assert.Equal(VoiceOverTickAction.StartRecording, actions[^1]);
        Assert.True(state.Recording);
        Assert.Equal(VoiceOverTickAction.Update, VoiceOverSteps.Tick(state, failed: false).Action);
    }

    [Fact]
    public void Grabando_un_error_de_escritura_para_en_el_tick_siguiente()
    {
        VoiceOverTickState state = new(30, 0, Recording: true);
        for (int i = 0; i < 5; i++)
        {
            (state, VoiceOverTickAction action) = VoiceOverSteps.Tick(state, failed: false);
            Assert.Equal(VoiceOverTickAction.Update, action);
        }

        Assert.Equal(VoiceOverTickAction.Stop, VoiceOverSteps.Tick(state, failed: true).Action);
    }

    [Fact]
    public void En_la_cuenta_atras_no_se_mira_el_error_porque_el_grabador_no_empezo()
    {
        VoiceOverTickState state = VoiceOverTickState.Countdown(3);
        for (int i = 0; i < 29; i++)
        {
            (state, VoiceOverTickAction action) = VoiceOverSteps.Tick(state, failed: true);
            Assert.NotEqual(VoiceOverTickAction.Stop, action);
        }

        Assert.Equal(VoiceOverTickAction.StartRecording, VoiceOverSteps.Tick(state, failed: true).Action);
    }

    [Theory]
    [InlineData(VoiceOverOutcome.Discard, true)]
    [InlineData(VoiceOverOutcome.ReportFailure, true)]
    [InlineData(VoiceOverOutcome.Empty, true)]
    [InlineData(VoiceOverOutcome.Insert, false)]
    public void Solo_la_toma_que_entra_en_la_linea_de_tiempo_se_queda(VoiceOverOutcome outcome, bool deleted)
    {
        string path = Path.Combine(_folder, "toma.wav");
        Assert.Equal(deleted ? path : null, VoiceOverSteps.FileToDelete(outcome, path));
    }

    [Theory]
    [InlineData(false, VoiceOverOutcome.ReportFailure)]
    [InlineData(true, VoiceOverOutcome.Discard)]
    public void Si_Finish_falla_se_borra_el_WAV_parcial_tras_soltarlo(bool cancelled, VoiceOverOutcome expected)
    {
        string path = Path.Combine(_folder, "toma.wav");
        var capture = new FakeCapture();
        var recorder = new VoiceOverRecorder(capture, path, Time.Zero, _ => Time.Zero);
        recorder.Begin();
        capture.Push(new float[4800]);
        Assert.True(File.Exists(path));
        var diskFull = new IOException("No queda espacio en el disco.");

        VoiceOverClosed closed = VoiceOverSteps.Close(cancelled, recorder.Path, () =>
        {
            // Como con el disco lleno: el grabador ya creó y escribió el WAV, y Finish lanza sin dar toma.
            recorder.Finish();
            throw diskFull;
        }, recorder.Dispose, File.Delete);

        Assert.Equal(expected, closed.Outcome);
        Assert.Null(closed.Take);
        Assert.Same(diskFull, closed.Failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Una_toma_sin_muestras_se_borra_y_una_con_muestras_se_queda()
    {
        string empty = Path.Combine(_folder, "vacia.wav");
        var silent = new VoiceOverRecorder(new FakeCapture(), empty, Time.Zero, _ => null);
        VoiceOverClosed closed = VoiceOverSteps.Close(false, silent.Path, silent.Finish, silent.Dispose, File.Delete);
        Assert.Equal(VoiceOverOutcome.Empty, closed.Outcome);
        Assert.False(File.Exists(empty));

        string full = Path.Combine(_folder, "llena.wav");
        var capture = new FakeCapture();
        var recorder = new VoiceOverRecorder(capture, full, Time.Zero, _ => Time.Zero);
        recorder.Begin();
        capture.Push(new float[4800]);
        closed = VoiceOverSteps.Close(false, recorder.Path, recorder.Finish, recorder.Dispose, File.Delete);
        Assert.Equal(VoiceOverOutcome.Insert, closed.Outcome);
        Assert.Equal(4800, closed.Take!.Frames);
        Assert.True(File.Exists(full));
    }

    [Fact]
    public void Se_borra_despues_de_soltar_el_archivo()
    {
        var calls = new List<string>();
        VoiceOverSteps.Close(false, "toma.wav", () => throw new IOException("lleno"), () => calls.Add("dispose"), p => calls.Add("delete " + p));
        Assert.Equal(["dispose", "delete toma.wav"], calls);
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private CapturePacketHandler? _handler;
        private long _qpc;

        public int SampleRate => Rate;

        public string DeviceName => "Micrófono de prueba";

        public void StartCapture(CapturePacketHandler handler) => _handler = handler;

        public void Push(float[] samples)
        {
            _handler?.Invoke(samples, _qpc, false);
            _qpc += samples.Length * 10_000_000L / Rate;
        }

        public void StopCapture() => _handler = null;

        public void Dispose()
        {
        }
    }
}
