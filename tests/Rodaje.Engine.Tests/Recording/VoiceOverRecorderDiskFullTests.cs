using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;
using Rodaje.Media.Export;
using Xunit;

namespace Rodaje.Engine.Tests.Recording;

/// <summary>
/// Con el disco lleno de verdad (<c>/dev/full</c>), <see cref="VoiceOverRecorder.Finish"/> lanza, pero
/// <see cref="VoiceOverRecorder.Dispose"/> no: el volcado final del búfer vuelve a fallar al cerrar el archivo, y eso no
/// puede saltarse el borrado del WAV ni soltar la captura. El WAV es un enlace simbólico a <c>/dev/full</c> en una
/// carpeta temporal, para que el borrado quite el enlace y no el dispositivo.
/// </summary>
public sealed class VoiceOverRecorderDiskFullTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-voz-lleno-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [DevFullFact]
    public void With_the_disk_full_Finish_throws_and_Dispose_does_not_and_deletes_the_WAV()
    {
        string path = LinkToDevFull("toma.wav");
        var capture = new FakeCapture();
        var recorder = new VoiceOverRecorder(capture, path, Time.Zero, _ => Time.Zero);
        recorder.Begin();
        capture.Push(new float[4800]);

        Assert.ThrowsAny<IOException>(recorder.Finish);

        Exception? thrown = Record.Exception(recorder.Dispose);
        Assert.Null(thrown);
        Assert.True(capture.Disposed);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
        Assert.True(File.Exists(DevFullFactAttribute.Device));
    }

    [DevFullFact]
    public void A_WAV_writer_that_could_not_complete_deletes_its_file_when_disposed_with_the_disk_full()
    {
        string path = LinkToDevFull("mezcla.wav");
        var writer = new WaveFileWriter(path, new ExportAudioFormat(Rate, 2, ExportSampleFormat.PcmFloat));
        writer.Write(new float[960]);

        Exception? thrown = Record.Exception(writer.Dispose);
        Assert.Null(thrown);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
        Assert.True(File.Exists(DevFullFactAttribute.Device));
    }

    private string LinkToDevFull(string name)
    {
        string path = Path.Combine(_folder, name);
        File.CreateSymbolicLink(path, DevFullFactAttribute.Device);
        return path;
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private CapturePacketHandler? _handler;
        private long _qpc;

        public int SampleRate => Rate;

        public string DeviceName => "Micrófono de prueba";

        public bool Disposed { get; private set; }

        public void StartCapture(CapturePacketHandler handler) => _handler = handler;

        public void Push(float[] samples)
        {
            _handler?.Invoke(samples, _qpc, false);
            _qpc += samples.Length * 10_000_000L / Rate;
        }

        public void StopCapture() => _handler = null;

        public void Dispose() => Disposed = true;
    }
}
