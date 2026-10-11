using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Media.Export;
using Rodaje.Render.Export;

namespace Rodaje.Export.Tests.Contract;

/// <summary>Las firmas del motor, el render y el sink encajan entre sí; la implementación es de los agentes A–C.</summary>
public sealed class PipelineContractTests
{
    private static Project EmptyProject() => new()
    {
        Id = "p",
        Name = "Exportar",
        Sequences = [new Sequence { Id = "sec", Name = "Principal", FrameRate = Rational.Ntsc30, Width = 1920, Height = 1080 }],
    };

    [Fact]
    public void Offline_timeline_opens_the_sequence_and_checks_the_frame_before_rendering()
    {
        Project project = EmptyProject();
        project.Sequences[0].VideoTracks.Add(new Track
        {
            Id = "v1", Kind = TrackKind.Video, Name = "Video 1",
            Clips = { new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1) } },
        });
        project.Media.Add(new MediaRef { Id = "m", Path = "falta.mp4" });

        using OfflineTimeline timeline = OfflineTimeline.Open(project, "sec", null, new OfflineTimelineOptions { MissingContent = MissingContentPolicy.ContinueWithout });

        Assert.Equal(("sec", 1920, 1080, Rational.Ntsc30, 30L), (timeline.SequenceId, timeline.Width, timeline.Height, timeline.FrameRate, timeline.FrameCount));
        Assert.Equal(Time.FromSeconds(1), timeline.Duration);
        Assert.Equal(0, timeline.MissingContent);
        Assert.Null(timeline.Device);
        Assert.Equal(MissingContentPolicy.ContinueWithout, timeline.Options.MissingContent);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Render(30, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.RenderAt(Time.FromSeconds(1), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => timeline.Render(0, new CancellationToken(canceled: true)));
        Assert.Throws<OperationCanceledException>(() => timeline.RenderAt(Time.Zero, new CancellationToken(canceled: true)));
        Assert.Throws<NotImplementedException>(() => timeline.Render(0, CancellationToken.None));
        Assert.Throws<NotImplementedException>(() => timeline.RenderAt(Time.Zero, CancellationToken.None));
        Assert.Throws<KeyNotFoundException>(() => OfflineTimeline.Open(project, "otra", null));

        timeline.Dispose();
        Assert.Throws<ObjectDisposedException>(() => timeline.Render(0, CancellationToken.None));
    }

    [Fact]
    public void Offline_audio_reads_the_mix_continuously_with_silence_where_there_are_no_clips()
    {
        using OfflineAudio audio = OfflineAudio.Open(EmptyProject(), "sec");
        var block = new float[960];
        block.AsSpan().Fill(1f);

        Assert.Equal(480, audio.Read(block));
        Assert.All(block, s => Assert.Equal(0f, s));
        Assert.Equal(480, audio.Position);
        audio.Seek(Time.FromSeconds(1));
        Assert.Equal(48_000, audio.Position);
        Assert.Equal((48_000, 0L), (OfflineAudio.SampleRate, audio.Errors));

        Assert.Throws<ArgumentOutOfRangeException>(() => audio.WriteWave("a.wav", Time.FromSeconds(1), Time.Zero, ExportAudioFormat.Default, 0, null, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.WriteWave("a.wav", Time.Zero, Time.FromSeconds(1), ExportAudioFormat.Default, double.NaN, null, default));
        Assert.Throws<NotImplementedException>(() => audio.WriteWave("a.wav", Time.Zero, Time.FromSeconds(1), ExportAudioFormat.Default, 0, null, default));
        Assert.Throws<NotImplementedException>(() => audio.MeasureLoudness(Time.Zero, Time.FromSeconds(1), null, default));

        audio.Dispose();
        audio.Dispose();
        Assert.Throws<ObjectDisposedException>(() => audio.Read(block));
        Assert.Throws<ObjectDisposedException>(() => audio.Seek(Time.Zero));
    }

    [Fact]
    public void Loudness_gain_reaches_the_target_without_passing_the_true_peak()
    {
        Assert.Equal(6, new LoudnessMeasurement(-20, -10, 5).GainTo(-14));
        Assert.Equal(3, new LoudnessMeasurement(-20, -4, 5).GainTo(-14));
        Assert.Equal(0, new LoudnessMeasurement(double.NegativeInfinity, double.NegativeInfinity, 0).GainTo(-14));
        Assert.Equal(-9, new LoudnessMeasurement(-14, double.NegativeInfinity, 0).GainTo(-23));
    }

    [Fact]
    public void Frame_converter_checks_its_arguments()
    {
        var format = new ExportVideoFormat(1920, 1080, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        Assert.Throws<ArgumentNullException>(() => new ExportFrameConverter(null!, 1920, 1080, format));
    }

    [Fact]
    public void A_sink_double_receives_frames_in_order_and_ends_with_the_packed_bytes()
    {
        var format = new ExportVideoFormat(4, 2, Rational.Fps30, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        var request = new ExportSinkRequest("salida.mp4", ExportContainer.Mp4, format, Support.TestEncoders.H264Nvenc, [], ExportAudioFormat.Default,
            "mezcla.wav", Support.TestEncoders.AacMf, [], 3)
        { Metadata = new ExportMetadata { Title = "Prueba", Chapters = [new ExportChapter(Time.Zero, Time.FromSeconds(1), "Inicio")] } };
        Assert.True(request.HasVideo && request.HasAudio && !request.IsAnalysisPass);
        Assert.True((request with { PassCount = 2 }).IsAnalysisPass);

        using var sink = new MemorySink();
        sink.Start(request, default);
        var luma = new byte[8];
        var chroma = new byte[4];
        for (long i = 0; i < 3; i++)
        {
            luma[0] = (byte)i;
            sink.WriteVideo(new ExportVideoFrame(i, format, luma, 4, chroma, 4), default);
        }

        ExportSinkResult result = sink.Complete(default);
        Assert.Equal(new ExportSinkResult("salida.mp4", 36, 3), result);
        Assert.Equal(3, sink.FramesEncoded);
        Assert.Equal(2, sink.Bytes[24]);
    }

    private sealed class MemorySink : IExportSink
    {
        private ExportSinkRequest? _request;

        public List<byte> BytesList { get; } = [];

        public byte[] Bytes => [.. BytesList];

        public long FramesEncoded { get; private set; }

        public void Start(ExportSinkRequest request, CancellationToken cancellationToken) => _request = request;

        public void WriteVideo(in ExportVideoFrame frame, CancellationToken cancellationToken)
        {
            Assert.Equal(FramesEncoded, frame.Index);
            var packed = new byte[frame.Format.FrameBytes];
            frame.CopyPacked(packed);
            BytesList.AddRange(packed);
            FramesEncoded++;
        }

        public ExportSinkResult Complete(CancellationToken cancellationToken) => new(_request!.OutputPath, BytesList.Count, FramesEncoded);

        public void Abort() => BytesList.Clear();

        public void Dispose() => Abort();
    }
}
