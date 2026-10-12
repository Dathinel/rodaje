using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.Subtitles.Tests;

/// <summary>El módulo con el modelo de verdad sobre voz real (FLEURS es_419). Cifras en la salida de cada prueba.</summary>
public sealed class SubtitleModuleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Audio_at_the_wrong_rate_is_rejected_without_closing_the_module()
    {
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        Assert.Equal("modulo-subtitulos", module.Client.Module.Id);
        SharedMemoryRegion region = module.Client.CreateRegion(48000 * 4);
        SharedBufferRef buffer = region.Describe(0, 48000 * 4, audio: new AudioLayout(48000, 1, 48000));
        ModuleException error = await Assert.ThrowsAnyAsync<ModuleException>(() => module.Client.InvokeAsync("subtitulos", buffers: [buffer]));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        error = await Assert.ThrowsAnyAsync<ModuleException>(() => module.Client.InvokeAsync("subtitulos", Json("""{"device":"npu"}"""), [buffer]));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        await module.Client.PingAsync();
    }

    [ModelFact(Corpus = true)]
    public async Task Transcribes_real_speech_with_ordered_word_times()
    {
        (float[] samples, List<Utterance> utterances) = Corpus.Build(30);
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        List<ModuleProgress> progress = [];
        ModuleResult result = await module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(language: "auto"),
            [ModuleHarness.Upload(module.Client, samples)], new Progress<ModuleProgress>(p => { lock (progress) { progress.Add(p); } }));
        Transcript transcript = ModuleHarness.Parse(result);
        (double wer, List<double> starts) = Score(transcript, utterances);
        output.WriteLine("Desvíos: " + string.Join(", ", starts.Select(Corpus.Ms)));
        output.WriteLine($"{transcript.Device}, idioma {transcript.Language}, {transcript.Segments.Count} segmentos, WER {wer:P1}, {progress.Count} avisos de avance");
        foreach (TranscriptSegment s in transcript.Segments)
        {
            output.WriteLine($"[{s.Start:0.00}–{s.End:0.00}] {s.Text} | {string.Join(' ', s.Words.Select(w => $"{w.Text}@{w.Start:0.00}"))}");
        }

        output.WriteLine("Referencia: " + string.Join(" / ", utterances.Select(u => $"{u.SpeechStart:0.00} {u.Text}")));
        output.WriteLine("Avance: " + string.Join(", ", progress.Select(p => $"{p.Stage}:{p.Fraction:0.00}").Distinct()));

        Assert.Equal("es", transcript.Language);
        Assert.True(wer < 0.25, $"WER {wer:P1}");
        Assert.NotEmpty(progress);
        double last = 0;
        foreach (TranscriptSegment s in transcript.Segments)
        {
            Assert.NotEmpty(s.Words);
            foreach (TranscriptWord w in s.Words)
            {
                Assert.True(w.Start >= last - 1e-9 && w.End >= w.Start && w.Start >= s.Start && w.End <= s.End + 1e-9, $"{w} en {s.Start}–{s.End}");
                Assert.False(string.IsNullOrWhiteSpace(w.Text));
                last = w.Start;
            }
        }
    }

    [ModelFact(Corpus = true)]
    public async Task A_large_result_goes_to_the_output_buffer()
    {
        (float[] samples, _) = Corpus.Build(15);
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        SharedMemoryRegion outRegion = module.Client.CreateRegion(1 << 20);
        ModuleResult result = await module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(),
            [ModuleHarness.Upload(module.Client, samples), outRegion.Describe(0, 1 << 20)]);
        int bytes = result.Value!.Value.GetProperty("bytes").GetInt32();
        Transcript transcript = JsonSerializer.Deserialize(outRegion.GetSpan(0, bytes), TranscriptJson.Default.Transcript)!;
        Assert.NotEmpty(transcript.Segments);
    }

    [ModelFact(Corpus = true)]
    [Trait("Categoria", "TiempoReal")]
    public async Task Cancelling_mid_transcription_is_confirmed_fast_and_the_module_keeps_serving()
    {
        (float[] samples, _) = Corpus.Build(180);
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        SharedBufferRef audio = ModuleHarness.Upload(module.Client, samples);
        using CancellationTokenSource cts = new();
        TaskCompletionSource transcribing = new();
        Task call = module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(), [audio],
            new Progress<ModuleProgress>(p => { if (p.Stage == "transcribiendo") transcribing.TrySetResult(); }), cts.Token);
        await transcribing.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(2000);
        Stopwatch watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        output.WriteLine($"Cancelación a mitad de la transcripción: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.ElapsedMilliseconds} ms");
        Assert.False(module.Exited.IsCompleted, "el módulo no debe morir por cancelar");
        await module.Client.PingAsync();
    }

    [ModelFact(Corpus = true)]
    public async Task A_module_killed_mid_transcription_fails_the_call_and_a_new_one_works()
    {
        (float[] samples, _) = Corpus.Build(60);
        await using (ModuleProcess module = await ModuleHarness.StartAsync())
        {
            Task call = module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(), [ModuleHarness.Upload(module.Client, samples)]);
            await Task.Delay(1500);
            Process.GetProcessById(module.ProcessId).Kill();
            Stopwatch watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<ModuleDisconnectedException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            output.WriteLine($"Muerte detectada en {watch.ElapsedMilliseconds} ms");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        }

        (float[] shortAudio, _) = Corpus.Build(10);
        await using ModuleProcess again = await ModuleHarness.StartAsync();
        Transcript transcript = ModuleHarness.Parse(await again.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(),
            [ModuleHarness.Upload(again.Client, shortAudio)]));
        Assert.NotEmpty(transcript.Segments);
    }

    /// <summary>Criterios de la fase 7: 10 min de voz en ≤1,5 min con GPU, WER ≤12 %, inicio de palabra con error mediano ≤150 ms.</summary>
    [ModelFact(Corpus = true, LongRunning = true)]
    [Trait("Categoria", "TiempoReal")]
    public async Task Ten_minutes_of_spanish_on_the_gpu_meet_the_phase_criteria()
    {
        (float[] samples, List<Utterance> utterances) = Corpus.Build(600);
        double seconds = samples.Length / (double)Corpus.Rate;
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        SharedBufferRef audio = ModuleHarness.Upload(module.Client, samples);
        Stopwatch watch;
        double peak;
        ModuleResult result;
        await using (VramSampler vram = new(module.ProcessId))
        {
            watch = Stopwatch.StartNew();
            result = await module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters(), [audio]);
            watch.Stop();
            peak = vram.PeakMb;
        }

        Transcript transcript = ModuleHarness.Parse(result);
        (double wer, List<double> starts) = Score(transcript, utterances);
        double median = Corpus.Percentile(starts.Select(Math.Abs), 50);
        output.WriteLine(Report("GPU", transcript, seconds, watch.Elapsed, wer, starts, peak));
        Transcript raw = ModuleHarness.Parse(await module.Client.InvokeAsync("subtitulos", Json($$"""{"model":{{JsonSerializer.Serialize(Corpus.ModelPath)}},"language":"es","onsets":false}"""), [audio]));
        List<double> rawStarts = Score(raw, utterances).StartErrors;
        output.WriteLine($"Solo DTW, sin el ajuste por energía: |error| mediana {Corpus.Ms(Corpus.Percentile(rawStarts.Select(Math.Abs), 50))}, sesgo mediano {Corpus.Ms(Corpus.Percentile(rawStarts, 50))}");

        Assert.Equal("vulkan", transcript.Device);
        Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(90 * seconds / 600), $"{watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(wer <= 0.12, $"WER {wer:P1}");
        Assert.True(median <= 0.150, $"mediana {Corpus.Ms(median)}");
        // El ajuste por energía usa un detector parecido al de la verdad: el DTW solo es la cifra independiente.
        double rawMedian = Corpus.Percentile(rawStarts.Select(Math.Abs), 50);
        Assert.True(rawMedian <= 0.150, $"mediana solo con DTW {Corpus.Ms(rawMedian)}");
    }

    /// <summary>CPU de respaldo con el modelo menor (D-034): 3 min de voz para extrapolar el criterio de ≤8 min por cada 10.</summary>
    [ModelFact(Corpus = true, LongRunning = true)]
    [Trait("Categoria", "TiempoReal")]
    public async Task The_cpu_fallback_is_fast_enough()
    {
        Assert.NotNull(Corpus.CpuModelPath);
        (float[] samples, List<Utterance> utterances) = Corpus.Build(180);
        double seconds = samples.Length / (double)Corpus.Rate;
        await using ModuleProcess module = await ModuleHarness.StartAsync();
        Stopwatch watch = Stopwatch.StartNew();
        Transcript transcript = ModuleHarness.Parse(await module.Client.InvokeAsync("subtitulos", ModuleHarness.Parameters("cpu"),
            [ModuleHarness.Upload(module.Client, samples)]));
        watch.Stop();
        (double wer, List<double> starts) = Score(transcript, utterances);
        output.WriteLine(Report("CPU", transcript, seconds, watch.Elapsed, wer, starts, double.NaN));
        Assert.Equal("cpu", transcript.Device);
        // 12-oct, Ryzen 7 7435HS con la máquina cargada: small q5_1 3,9× y WER 10,2 %; base q5_1 8,6× y WER 28,5 %;
        // large-v3-turbo en la CPU 0,8× y WER 3,6 %.
        Assert.True(watch.Elapsed.TotalSeconds <= 0.8 * seconds, $"{watch.Elapsed.TotalSeconds:0.0} s para {seconds:0} s");
        Assert.True(wer <= 0.15, $"WER {wer:P1}");
    }

    /// <summary>Matriz de formatos: el módulo no veía el modelo en <c>RODAJE_ENTORNO\modelos</c>, donde lo buscan los demás.</summary>
    [Fact]
    public void The_model_is_also_found_where_the_other_modules_look()
    {
        using var dir = new TempFolder();
        string file = $"modelo-{Guid.NewGuid():N}.bin";
        Directory.CreateDirectory(Path.Combine(dir.Path, "modelos"));
        File.WriteAllBytes(Path.Combine(dir.Path, "modelos", file), [0]);
        string? before = Environment.GetEnvironmentVariable("RODAJE_ENTORNO");
        try
        {
            Environment.SetEnvironmentVariable("RODAJE_ENTORNO", dir.Path);
            Assert.Equal(Path.Combine(dir.Path, "modelos", file), Transcriber.FindModel(null, file));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RODAJE_ENTORNO", before);
        }

        Assert.Throws<Rodaje.Modules.Protocol.ModuleCallException>(() => Transcriber.FindModel(null, file));
    }

    [Fact]
    public void The_cpu_model_is_the_smaller_one_when_present_and_dtw_heads_follow_the_model()
    {
        using var dir = new TempFolder();
        string large = Path.Combine(dir.Path, Transcriber.ModelFile);
        string small = Path.Combine(dir.Path, Transcriber.CpuModelFile);
        File.WriteAllBytes(large, [0]);
        Assert.Equal(large, Transcriber.CpuModel(Transcriber.Options.Parse(null) with { CpuModel = small }, large));
        File.WriteAllBytes(small, [0]);
        Assert.Equal(small, Transcriber.CpuModel(Transcriber.Options.Parse(null) with { CpuModel = small }, large));
        Assert.Equal(Whisper.net.WhisperAlignmentHeadsPreset.Small, Transcriber.HeadsFor(small));
        Assert.Equal(Whisper.net.WhisperAlignmentHeadsPreset.LargeV3Turbo, Transcriber.HeadsFor(large));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("rodaje-subtitulos ñ ").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>WER sobre todo el texto y, por frase, el desvío (hipótesis − verdad) del inicio de su primera palabra.</summary>
    private static (double Wer, List<double> StartErrors) Score(Transcript transcript, List<Utterance> utterances)
    {
        List<string> reference = [];
        List<int> firstWord = [];
        foreach (Utterance u in utterances)
        {
            string[] words = Corpus.Normalize(u.Text);
            firstWord.Add(words.Length > 0 ? reference.Count : -1);
            reference.AddRange(words);
        }

        List<string> hypothesis = [];
        List<double> hypStart = [];
        foreach (TranscriptWord w in transcript.Segments.SelectMany(s => s.Words))
        {
            foreach (string part in Corpus.Normalize(w.Text))
            {
                hypothesis.Add(part);
                hypStart.Add(w.Start);
            }
        }

        (double wer, int[] match) = Corpus.Align(reference, hypothesis);
        List<double> errors = [];
        for (int i = 0; i < utterances.Count; i++)
        {
            if (firstWord[i] >= 0 && match[firstWord[i]] is int j and >= 0)
            {
                errors.Add(hypStart[j] - utterances[i].SpeechStart);
            }
        }

        return (wer, errors);
    }

    private static string Report(string device, Transcript t, double seconds, TimeSpan elapsed, double wer, List<double> starts, double vramMb)
    {
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{device} ({t.Device}): {seconds / 60:0.0} min de voz en {elapsed.TotalSeconds:0.0} s = {seconds / elapsed.TotalSeconds:0.0}× tiempo real");
        sb.AppendLine(CultureInfo.InvariantCulture, $"WER {wer:P1}; {t.Segments.Count} segmentos, {t.Segments.Sum(s => s.Words.Count)} palabras");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Inicio de la primera palabra de {starts.Count} frases: |error| mediana {Corpus.Ms(Corpus.Percentile(starts.Select(Math.Abs), 50))}, p90 {Corpus.Ms(Corpus.Percentile(starts.Select(Math.Abs), 90))}, sesgo mediano {Corpus.Ms(Corpus.Percentile(starts, 50))}");
        if (!double.IsNaN(vramMb))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"VRAM dedicada del proceso, pico: {vramMb:0} MB");
        }

        return sb.ToString();
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
