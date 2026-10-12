using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.App.Services;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Translate;
using Rodaje.App.Services.Voice;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Subtitles;
using Rodaje.App.Views.Vision;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Ai;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// Cada funciÃ³n de IA con los mÃ³dulos de verdad y el perfil de IA de este equipo, de la VRAM real y simulada a 2 GB, y sin
/// perfil (como antes de D-037), para medir antes y despuÃ©s (<c>Categoria=Matriz</c>, solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>).
/// La tabla queda en <c>TestResults\matriz\ia-perfil-&lt;perfil&gt;.md</c>.
/// </summary>
[Collection(MatrixGroup.Name)]
[Trait("Categoria", "Matriz")]
public sealed class AiProfileRealTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    public static TheoryData<string> Profiles() => ["antes", "real", "2gb", "cpu"];

    [MatrixAiTheory]
    [MemberData(nameof(Profiles))]
    public void Cada_funcion_termina_con_el_perfil(string profile) => UiThread.Run(async () =>
    {
        MatrixCase c = MatrixAiTests.Cases[0];
        (string? path, string? problem) = MatrixMedia.GetOrCreate(FfmpegTool.Default!, c);
        Assert.True(path is not null, problem);
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([path!]);
        MediaRef media = Assert.Single(session.Project.Media);
        Clip video = session.Sequence.VideoTracks.SelectMany(t => t.Clips).First();
        Clip audio = session.Sequence.AudioTracks.SelectMany(t => t.Clips).First();

        AiProfile? ai = profile switch
        {
            "antes" => null,
            "real" => Profile(null),
            "2gb" => Profile(2048),
            _ => Profile(0),
        };
        AiPlan? plan = ai?.Plan();
        var rows = new List<(string Step, string Where, double Estimate, double Seconds, string Result)>();
        var failures = new List<string>();
        await using ModuleManager manager = new(new ModuleManagerOptions
        {
            Roots = [new ModuleRoot(AudioAiCorpus.ModulesFolder!, ModuleOrigin.Development)],
            DedicatedVideoMemory = () => (GpuProfileReader.Current.Primary?.DedicatedVideoMemory) ?? 0,
            Ai = ai,
        });
        manager.Refresh();
        manager.Failed += (_, f) => failures.Add($"{f.Title}: {f.Message}");

        async Task Step(string name, AiEstimate? estimate, Func<Task<string>> work)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                string result = await work();
                rows.Add((name, estimate?.Where ?? "(v1)", estimate?.Seconds ?? double.NaN, watch.Elapsed.TotalSeconds, result));
            }
#pragma warning disable CA1031 // Se anota y la prueba falla al final con todo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                rows.Add((name, estimate?.Where ?? "(v1)", estimate?.Seconds ?? double.NaN, watch.Elapsed.TotalSeconds, "FALLÃ“: " + ex.Message));
                failures.Add($"{name}: {ex.Message}");
            }
        }

        ModuleDescriptor subtitles = Require(manager, AutoSubtitles.Function);
        Transcript? transcript = null;
        await Step("SubtÃ­tulos (12 s)", AutoSubtitles.Estimate(manager, subtitles, AutoSubtitles.Window(audio)), async () =>
        {
            transcript = await AutoSubtitles.RunAsync(manager, subtitles, path!, AutoSubtitles.Window(audio), "es", "SubtÃ­tulos", CancellationToken.None);
            int words = transcript.Segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.True(words >= 10, $"Solo {words} palabras.");
            return Fmt($"{words} palabras en {transcript.Device}");
        });

        if (transcript is not null)
        {
            SubtitleText[] texts = [.. transcript.Segments.Select((s, i) => new SubtitleText("s" + i.ToString(CultureInfo.InvariantCulture), s.Text))];
            var translator = new ModuleSubtitleTranslator(manager);
            await Step(Fmt($"Traducir ({texts.Sum(t => t.Text.Length)} car.)"), translator.Estimate(texts), async () =>
            {
                TrackTranslation result = await translator.TranslateAsync(texts, "es", "en", "Traducir", CancellationToken.None);
                Assert.Empty(result.Untranslated);
                return Fmt($"{result.Texts.Count} textos: Â«{result.Texts.Values.First().Trim()}Â»");
            });
        }

        ModuleDescriptor vision = Require(manager, VisionTools.RemoveBackgroundFunction);
        Clip Short(double seconds) => new() { Id = video.Id, MediaId = video.MediaId, Start = video.Start, SourceIn = video.SourceIn, SourceOut = video.SourceIn + Time.FromSeconds(seconds) };
        foreach ((bool high, double seconds) in new[] { (false, 3.0), (true, 0.5) })
        {
            Clip clip = Short(seconds);
            await Step(Fmt($"Quitar fondo {(high ? "alta" : "normal")} ({VisionTools.FrameSpan(clip, media.FrameRate!.Value).Count} cuadros)"),
                VisionTools.EstimateRemoveBackground(manager, clip, media.FrameRate!.Value, high), async () =>
                {
                    MatteVideo matte = await VisionTools.RemoveBackgroundAsync(manager, vision, FfmpegExecutable.Locate(), media.Path, clip, media.FrameRate!.Value,
                        high, _folder.Path, "Quitar fondo", CancellationToken.None);
                    Assert.True(matte.Frames > 0 && File.Exists(matte.Path));
                    return Fmt($"{matte.Frames} mÃ¡scaras {matte.Width}Ã—{matte.Height}");
                });
        }

        Clip reframe = Short(3);
        await Step("Reencuadre (3 s, personas)", VisionTools.EstimateReframe(manager, reframe), async () =>
        {
            ReframePlan result = await VisionTools.ReframeAsync(manager, vision, media.Path, reframe, 9 / 16.0, faces: false, 0.5, "Reencuadre", CancellationToken.None);
            return Fmt($"{result.Keys.Count} keyframes de {result.Frames} cuadros");
        });

        ModuleDescriptor noise = Require(manager, NoiseReduction.Function);
        (Time Start, Time End) noiseWindow = NoiseReduction.Window(audio, media.Duration);
        await Step("Reducir ruido (12 s)", NoiseReduction.Estimate(manager, noiseWindow), async () =>
        {
            CleanAudio clean = await NoiseReduction.RunAsync(manager, noise, path!, noiseWindow, _folder.Path, "Reducir ruido", CancellationToken.None);
            return Fmt($"{clean.Duration.ToSeconds():0.0} s");
        });

        var synth = new ModuleVoiceSynthesizer(manager);
        const string Sentence = "Hola. Esta es una prueba de voz con el perfil de inteligencia artificial de este equipo.";
        await Step("Texto a voz", synth.Estimate(Sentence), async () =>
        {
            SynthesizedVoice voice = await synth.SynthesizeAsync(new VoiceSettings(Sentence), "Voz", CancellationToken.None);
            return Fmt($"{voice.Seconds:0.0} s de voz");
        });

        var table = new StringBuilder();
        table.AppendLine(Fmt($"### Perfil Â«{profile}Â»: {ai?.Machine.Describe() ?? "sin perfil (v1)"}"));
        if (plan is not null)
        {
            table.AppendLine(Fmt($"Presupuesto de VRAM {plan.GpuBudgetMb} MB; Whisper {plan.Subtitles.Model} en {plan.Subtitles.Device} Ã—{plan.Subtitles.Parallel}; traductor {plan.Translate.GpuLayers} capas; visiÃ³n {plan.Vision.Device}, lado {plan.Vision.MaskSide}, alta en GPU: {plan.Vision.HighOnGpu}."));
        }

        table.AppendLine();
        table.AppendLine("| Paso | DÃ³nde | Estimado | Real | Resultado |");
        table.AppendLine("|---|---|---|---|---|");
        foreach ((string step, string where, double estimate, double seconds, string result) in rows)
        {
            table.AppendLine(Fmt($"| {step} | {where} | {(double.IsNaN(estimate) ? "â€”" : estimate.ToString("0.0", CultureInfo.InvariantCulture) + " s")} | {seconds:0.0} s | {result.Replace('|', '/')} |"));
        }

        string folder = MatrixTable.Folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"ia-perfil-{profile}.md"), table.ToString());
        output.WriteLine(table.ToString());
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }, TimeSpan.FromMinutes(40));

    private static AiProfile Profile(int? vramMb) =>
        new(() => GpuProfileReader.Current, SystemMemory.Read, () => AiQuality.Auto, new AiMeasuredRates("prueba"), vramMb);

    private static ModuleDescriptor Require(ModuleManager manager, string function) =>
        manager.FindByFunction(function) ?? throw new InvalidOperationException($"No hay mÃ³dulo compilado con Â«{function}Â» ({string.Join(" ", manager.Problems)}).");

    private static string Fmt(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
