using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;
using Xunit;

namespace Rodaje.Engine.Tests.Recording;

/// <summary>
/// Regresión (revisión de #100): si el disco se llenaba a mitad de la toma, el grabador guardaba el error en silencio y el
/// diálogo seguía contando hasta que se pulsaba «Detener»; además, el error salía aunque se hubiera cancelado. Ahora el
/// error se ve en <see cref="VoiceOverRecorder.Failure"/> en cuanto ocurre, y cancelar lo descarta sin avisar.
/// </summary>
public sealed class VoiceOverRecorderFailureTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-voz-error-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_write_error_in_the_middle_of_the_take_is_visible_before_finishing()
    {
        var capture = new FakeCapture();
        int writes = 0;
        var diskFull = new IOException("No queda espacio en el disco.");
        using var recorder = new VoiceOverRecorder(capture, Path.Combine(_folder, "toma.wav"), Time.Zero, _ => Time.Zero, _ =>
        {
            if (++writes == 3)
            {
                throw diskFull;
            }
        });
        recorder.Begin();

        capture.Push(new float[480]);
        capture.Push(new float[480]);
        Assert.Null(recorder.Failure);

        capture.Push(new float[480]);
        Assert.Same(diskFull, recorder.Failure);

        // Después del error no se escribe nada más ni lanza en el hilo de captura.
        capture.Push(new float[480]);
        Assert.Equal(3, writes);

        IOException thrown = Assert.Throws<IOException>(recorder.Finish);
        Assert.Same(diskFull, thrown.InnerException);
    }

    [Fact]
    public void A_take_without_errors_reports_no_failure()
    {
        var capture = new FakeCapture();
        using var recorder = new VoiceOverRecorder(capture, Path.Combine(_folder, "toma.wav"), Time.Zero, _ => Time.Zero);
        recorder.Begin();
        capture.Push(new float[480]);
        Assert.Null(recorder.Failure);
        Assert.Equal(480, recorder.Finish().Frames);
        Assert.Null(recorder.Failure);
    }

    [Theory]
    [InlineData(true, true, 0, VoiceOverOutcome.Discard)]
    [InlineData(true, true, 480, VoiceOverOutcome.Discard)]
    [InlineData(true, false, 480, VoiceOverOutcome.Discard)]
    [InlineData(false, true, 480, VoiceOverOutcome.ReportFailure)]
    [InlineData(false, true, 0, VoiceOverOutcome.ReportFailure)]
    [InlineData(false, false, 0, VoiceOverOutcome.Empty)]
    [InlineData(false, false, 480, VoiceOverOutcome.Insert)]
    public void Cancelling_never_reports_the_failure(bool cancelled, bool failed, long frames, VoiceOverOutcome expected) =>
        Assert.Equal(expected, VoiceOverRecorder.Decide(cancelled, failed, frames));

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
