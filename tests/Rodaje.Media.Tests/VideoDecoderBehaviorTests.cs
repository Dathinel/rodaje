using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class VideoDecoderBehaviorTests
{
    [FfmpegLibraryFact]
    public void Frames_past_the_end_are_null_and_the_decoder_recovers()
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));
        int last = reference.Count - 1;

        using (DecodedFrame? frame = decoder.GetFrame(last))
        {
            Assert.Equal(reference[last], Hash(frame!));
        }

        Assert.Null(decoder.GetFrame(last + 1));
        Assert.Null(decoder.GetFrame(last + 500));
        using (DecodedFrame? frame = decoder.GetFrame(last))
        {
            Assert.Equal(reference[last], Hash(frame!));
        }

        using (DecodedFrame? frame = decoder.GetFrame(10))
        {
            Assert.Equal(reference[10], Hash(frame!));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.GetFrame(-1));
    }

    [FfmpegLibraryFact]
    public async Task Async_source_returns_the_same_frames()
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.Hevc);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.Hevc), DecodeModes.Options(DecodeMode.SoftwareCpu));
        foreach (int n in new[] { 100, 101, 7, 249 })
        {
            using DecodedFrame? frame = await ((IVideoFrameSource)decoder).GetFrameAsync(n, CancellationToken.None);
            Assert.Equal(reference[n], Hash(frame!));
        }

        Assert.Null(await ((IVideoFrameSource)decoder).GetFrameAsync(250, CancellationToken.None));
    }

    [FfmpegLibraryFact]
    public async Task Cancellation_is_honored_and_leaves_the_decoder_usable()
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        ValueTask<DecodedFrame?> pending = decoder.GetFrameAsync(150, cancelled.Token);
        Assert.True(pending.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Throws<OperationCanceledException>(() => decoder.GetFrame(150, cancelled.Token));

        // Cancelar a mitad de la decodificación es una carrera: o se cancela o entrega el cuadro correcto.
        for (int i = 0; i < 20; i++)
        {
            using var soon = new CancellationTokenSource(TimeSpan.FromTicks(i * 2000));
            int target = 47 + (i * 11 % 240);
            try
            {
                using DecodedFrame? frame = decoder.GetFrame(target, soon.Token);
                Assert.Equal(reference[target], Hash(frame!));
            }
            catch (OperationCanceledException)
            {
            }

            using DecodedFrame? next = decoder.GetFrame(target + 1);
            Assert.Equal(reference[target + 1], Hash(next!));
        }
    }

    [FfmpegLibraryFact]
    public void Sequential_requests_do_not_seek_and_far_jumps_do()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));
        for (int n = 0; n < 40; n++)
        {
            decoder.GetFrame(n)!.Dispose();
        }

        Assert.Equal(1, decoder.Statistics.Seeks);
        decoder.GetFrame(40 + 30)!.Dispose();
        Assert.Equal(1, decoder.Statistics.Seeks);
        decoder.GetFrame(250)!.Dispose();
        Assert.Equal(2, decoder.Statistics.Seeks);
        decoder.GetFrame(5)!.Dispose();
        Assert.Equal(3, decoder.Statistics.Seeks);
    }

    [FfmpegLibraryFact]
    public void Corrupted_packets_are_skipped_without_throwing()
    {
        string clip = TestClips.Corrupted(TestClips.H264);
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        int recovered = FirstKeyFrameAfterCorruption();
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.SoftwareCpu));
        int delivered = 0;
        for (int n = 0; n < TestClips.H264.Frames; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            if (frame is not null)
            {
                Assert.Equal(n, frame.Index);
                ulong hash = Hash(frame);
                if (n < TestClips.CorruptedFrames[0] - TestClips.H264.BFrames - 1 || n >= recovered)
                {
                    Assert.True(reference[n] == hash, $"El cuadro {n}, fuera de la zona dañada, no coincide con la referencia.");
                }

                delivered++;
            }
        }

        Assert.True(decoder.Statistics.DecodeErrors > 0, "El archivo dañado no produjo ningún error de decodificación.");
        Assert.True(delivered > TestClips.H264.Frames / 2, $"Solo se entregaron {delivered} cuadros.");
        foreach (int n in new[] { 200, 3, 150, 95, 299, 121 })
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            if (n >= recovered || n < TestClips.CorruptedFrames[0] - TestClips.H264.BFrames - 1)
            {
                Assert.True(frame is not null && reference[n] == Hash(frame),
                    $"El cuadro {n}, pedido de nuevo fuera de la zona dañada, no coincide con la referencia (recuperado desde {recovered}, {decoder.Statistics.Seeks} seeks).");
            }
        }
    }

    [FfmpegLibraryTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_seek_after_decoding_errors_restores_the_decoder_state(int threads)
    {
        // Con el H.264 de cualquier codificador (en la CI sin NVENC, libopenh264), tras paquetes dañados
        // avcodec_flush_buffers dejaba un estado que hacía salir mal incluso un cuadro posterior a un IDR.
        string clip = TestClips.Corrupted(TestClips.H264);
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.SoftwareCpu) with { SoftwareThreads = threads });
        for (int n = 0; n < TestClips.H264.Frames; n++)
        {
            decoder.GetFrame(n)?.Dispose();
        }

        decoder.GetFrame(150)?.Dispose();
        Assert.True(decoder.Statistics.DecodeErrors > 0, "El archivo dañado no produjo ningún error de decodificación.");
        for (int n = 0; n < 48; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.True(frame is not null && reference[n] == Hash(frame), $"El cuadro {n}, tras buscar desde la zona dañada, no coincide con la referencia (hilos: {threads}).");
        }
    }

    [FfmpegLibraryTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void Damage_still_inside_the_decoder_at_a_seek_does_not_leak_into_a_later_gop(int threads)
    {
        // Con hilos por cuadro el decodificador retiene paquetes (hasta ~15): parar justo antes de la zona dañada deja
        // los primeros paquetes dañados dentro, sin error ni cuadro marcado, cuando llega el seek.
        string clip = TestClips.Corrupted(TestClips.H264);
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        int stop = TestClips.CorruptedFrames[0] - TestClips.H264.BFrames - 4;
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.SoftwareCpu) with { SoftwareThreads = threads });
        for (int n = 0; n <= stop; n++)
        {
            decoder.GetFrame(n)?.Dispose();
        }

        int later = TestClips.KeyFrames(TestClips.H264).Last(k => k < TestClips.H264.Frames - 10);
        for (int n = later; n < TestClips.H264.Frames; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.True(frame is not null && reference[n] == Hash(frame), $"El cuadro {n}, en un GOP posterior a la zona dañada, no coincide con la referencia (hilos: {threads}).");
        }

        // Con hilos por cuadro (y núcleos de sobra para que el decodificador retenga paquetes), el drenaje antes del seek es lo
        // único que ve el daño en vuelo, sea cual sea el codificador del clip.
        if (threads == 0 && Environment.ProcessorCount >= 8)
        {
            Assert.True(decoder.Statistics.DecodeErrors > 0, "El seek no drenó los paquetes dañados que había dentro del decodificador.");
        }
    }

    [HardwareFact]
    public void Corrupted_packets_are_skipped_in_hardware_too()
    {
        string clip = TestClips.Corrupted(TestClips.H264);
        IReadOnlyList<ulong> reference = TestClips.LumaReference(TestClips.H264);
        int recovered = FirstKeyFrameAfterCorruption();
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.Hardware));
        int delivered = 0;
        foreach (int n in Enumerable.Range(0, TestClips.H264.Frames).Concat([10, 280, 140]))
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            delivered += frame is null ? 0 : 1;
            if (n >= recovered)
            {
                Assert.True(frame is not null && reference[n] == Hash(frame), $"El cuadro {n}, después de la zona dañada, no coincide con la referencia.");
            }
        }

        Assert.True(decoder.UsesHardwareDecoding);
        Assert.True(decoder.Statistics.DecodeErrors > 0, "El archivo dañado no produjo ningún error de decodificación.");
        Assert.True(delivered > TestClips.H264.Frames / 2, $"Solo se entregaron {delivered} cuadros.");
    }

    private static int FirstKeyFrameAfterCorruption()
    {
        int last = TestClips.CorruptedFrames.Max();
        return TestClips.KeyFrames(TestClips.H264).First(k => k > last + 2);
    }

    [FfmpegLibraryFact]
    public void Variable_frame_rate_maps_each_index_to_the_nearest_earlier_frame()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.VariableFrameRate(), DecodeModes.Options(DecodeMode.SoftwareCpu));
        Assert.True(decoder.Stream.IsVariableFrameRate);
        var sequential = new List<ulong>();
        for (int n = 0; n < decoder.FrameCount; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
            Assert.Equal(Time.FromFrames(n, decoder.FrameRate), frame.Pts);
            sequential.Add(Hash(frame));
        }

        foreach (int n in new[] { 45, 5, 89, 30, 31, 0, 60 })
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.Equal(sequential[n], Hash(frame!));
        }
    }

    [FfmpegLibraryFact]
    public void Calls_that_overlap_are_rejected()
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));
        using var start = new Barrier(2);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        void Work(int offset)
        {
            start.SignalAndWait();
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    decoder.GetFrame((offset + (i * 37)) % 300)?.Dispose();
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex);
                }
            }
        }

        var a = Task.Run(() => Work(0));
        var b = Task.Run(() => Work(150));
        Task.WaitAll(a, b);
        Assert.All(errors, e => Assert.Contains("solaparse", e.Message, StringComparison.Ordinal));
        using DecodedFrame? frame = decoder.GetFrame(123);
        Assert.Equal(TestClips.LumaReference(TestClips.H264)[123], Hash(frame!));
    }

    [FfmpegLibraryFact]
    public void A_file_without_video_cannot_be_opened_as_video()
    {
        MediaException error = Assert.Throws<MediaException>(() => VideoDecoder.Open(TestClips.MonoAudio44k()));
        Assert.Contains("video", error.Message, StringComparison.Ordinal);
    }

    [FfmpegLibraryFact]
    public void Disposed_decoder_rejects_requests()
    {
        VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(TestClips.H264), DecodeModes.Options(DecodeMode.SoftwareCpu));
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.GetFrame(0));
    }

    private static ulong Hash(DecodedFrame frame) => TestClips.HashLuma(FrameReadback.ReadLuma(frame).Pixels);
}
