using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.SafeZones;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Translate;
using Rodaje.App.Services.Voice;
using Rodaje.App.Tests.Matrix;
using Rodaje.App.Tests.Modules;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Analysis;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Raw;
using Rodaje.App.Views.Subtitles;
using Rodaje.App.Views.Vision;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Rodaje.Packages.Download;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Todo el catálogo publicado, en uso: cada complemento de contenido se instala y se usa en un proyecto real (LUT de cámara,
/// filtro, transición, estilo de título con su fuente, zonas, sonido, reencuadre 360) que se exporta con un preajuste
/// profesional entrelazado y con uno 360; y cada módulo de IA baja sus modelos del catálogo y hace su función.
/// </summary>
public sealed partial class CatalogoRealExtremoAExtremoTests
{
    private static readonly string[] Nuevos = ["rodaje-camaras", "rodaje-sonidos", "rodaje-preajustes-pro", "rodaje-360"];

    private static readonly string[] TodoElContenido =
        ["rodaje-transiciones", "rodaje-filtros", "rodaje-fuentes", "rodaje-titulos", "rodaje-zonas", .. Nuevos];

    private static readonly MatrixCase Toma = new("catalogo-toma-2x1", "MP4", "H.264 1920×960 25 fps", "AAC estéreo 48 kHz", ".mp4")
    {
        Width = 1920, Height = 960, Rate = new Rational(25, 1), Seconds = 4,
        Video = ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "20", "-g", "25", "-pix_fmt", "yuv420p"],
        Audio = ["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2"], Requires = "h264_nvenc",
    };

    [RedFact]
    public void Todo_el_contenido_se_instala_se_usa_en_un_proyecto_se_exporta_y_se_desinstala() => UiThread.Run(async () =>
    {
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        SignedIndex index = await downloader.GetIndexAsync();
        IReadOnlyDictionary<string, PackageVersion> available = await service.AvailableVersionsAsync();
        Assert.All(Nuevos, id => Assert.Equal("1.0.0", available[id].ToString()));
        Assert.All(service.Catalog.Entries, e => Assert.True(available.ContainsKey(e.Id), $"{e.Id} no está publicado."));
        Note($"Índice {index.Generated:O}: {index.Entries.Count} entradas; contenido nuevo: {string.Join(", ", Nuevos)}.");

        foreach (string id in TodoElContenido)
        {
            var watch = Stopwatch.StartNew();
            InstallResult result = await service.InstallFromCatalogAsync(service.Catalog.Find(id)!);
            Assert.Equal(InstallOutcome.Installed, result.Outcome);
            Note($"{id} {result.Package.Manifest.Version}: {Size(Latest(index, id).Size)} en {watch.Elapsed.TotalSeconds:0.0} s, {result.Package.Manifest.Items.Count} elementos.");
        }

        PackageScan installed = service.Installed;
        var loader = new InstalledContentLoader(() => service.Installed);
        (string? media, string? problem) = MatrixMedia.GetOrCreate(FfmpegTool.Default!, Toma);
        Assert.True(media is not null, problem);
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([media!]);
        Assert.True(session.AppendToTimeline(session.Project.Media[0].Id));
        Clip[] clips = [.. session.Sequence.VideoTracks[0].Clips.OrderBy(c => c.Start)];
        Assert.Equal(2, clips.Length);
        var applier = new ElementApplier(session, loader);
        LibraryElement Element(LibraryCategory category, string reference) =>
            Assert.Single(LibraryElements.For(category, service.Installed), e => e.Reference == reference);
        List<EffectInstance> EffectsOf(string clipId) => session.Sequence.FindClip(clipId)!.Value.Clip.Effects;

        // LUT de cámara: el filtro S-Log3 pone su LUT en el color del clip, y el gris del 18 % sale en 0,43.
        session.Select([clips[0].Id]);
        Assert.True(await applier.ApplyAsync(Element(LibraryCategory.Filters, "rodaje-camaras@1.0.0/s-log3")));
        EffectInstance color = Assert.Single(EffectsOf(clips[0].Id), e => e.Type == BuiltInEffects.Color);
        Assert.Equal("rodaje-camaras@1.0.0/lut-s-log3", color.Options[BuiltInEffects.ColorOptions.Lut]);
        LutTable lut = Assert.IsType<LutTable>(loader.LoadLut("rodaje-camaras@1.0.0/lut-s-log3"));
        double gray = Trilinear(lut, ((0.41055 * 1023) - 64) / 876);
        Assert.InRange(gray, 0.42, 0.44);
        Note($"rodaje-camaras: S-Log3 aplicado; LUT {lut.Size}³, el gris del 18 % (código 420) sale en {gray:0.000}.");

        // Reencuadre 360: el complemento activa el efecto y su paneo pone keyframes de guiñada en todo el clip.
        EffectCatalog.EnabledFeatures = EffectCatalog.FeaturesFrom(service.Installed);
        Assert.Contains(EffectFeatures.Video360, EffectCatalog.EnabledFeatures);
        Assert.Contains(EffectCatalog.For(TrackKind.Video), e => e.Type == BuiltInEffects.Reframe360);
        Assert.True(await applier.ApplyAsync(Element(LibraryCategory.Effects, "rodaje-360@1.0.0/paneo-completo")));
        EffectInstance reframe = Assert.Single(EffectsOf(clips[0].Id), e => e.Type == BuiltInEffects.Reframe360);
        List<Keyframe> yaw = reframe.Parameters[BuiltInEffects.Reframe360Parameters.Yaw].Keyframes;
        Assert.Equal(2, yaw.Count);
        Note($"rodaje-360: «Paneo de 360°» con {yaw.Count} keyframes de guiñada ({yaw[0].Value:0}° → {yaw[^1].Value:0}°).");

        // Filtro y transición en el segundo clip y en el corte.
        session.Select([clips[1].Id]);
        Assert.True(await applier.ApplyAsync(Element(LibraryCategory.Filters, "rodaje-filtros@1.1.0/cine")));
        Assert.Contains(EffectsOf(clips[1].Id), e => e.Type == BuiltInEffects.Vignette);
        LibraryElement transition = LibraryElements.For(LibraryCategory.Transitions, service.Installed).First(e => e.Reference.StartsWith("rodaje-transiciones@", StringComparison.Ordinal));
        session.Select([clips[0].Id]);
        Assert.True(await applier.ApplyAsync(transition));
        Note($"rodaje-filtros: «Cine» aplicado; rodaje-transiciones: «{transition.Name}» en el corte.");

        // Estilo de título con su fuente del paquete, y zonas del visor.
        Assert.True(await applier.AddTitleAsync(Element(LibraryCategory.Text, "rodaje-titulos@1.0.0/karaoke-pop")));
        IReadOnlyList<SafeZoneChoice> zones = SafeZoneCatalog.All(service.Installed);
        SafeZoneChoice zone = zones.First(z => z.Origin == SafeZoneOrigin.Package);
        Assert.Equal(zone, SafeZoneCatalog.Find(zones, zone.Reference));
        Note($"rodaje-titulos: «Karaoke pop» en el cursor; rodaje-zonas: {zones.Count(z => z.Origin == SafeZoneOrigin.Package)} juegos, «{zone.Name}» elegido.");

        // Un sonido: se copia a los medios del proyecto y entra como clip de audio.
        PackageSound sound = SoundDrag.Resolve(service.Installed, "rodaje-sonidos@1.0.0/swoosh-corto", out string? soundProblem)!;
        Assert.Null(soundProblem);
        string copy = SoundDrag.CopyInto(sound, Path.Combine(_folder.Path, "Proyecto.medios"));
        Assert.True(session.InsertSound(copy, sound, Time.FromSeconds(1), SoundDrag.TargetTrack(session.Sequence, null, Time.FromSeconds(1), sound.Duration)?.Id));
        (SoundInfo info, float[] samples) = ContentReaders.ReadSound(File.OpenRead(copy));
        Assert.Equal((48_000, 2), (info.SampleRate, info.Channels));
        Assert.InRange(samples.Max(Math.Abs), 0.03, 0.9);
        Note($"rodaje-sonidos: «{sound.Name}» ({info.DurationSeconds:0.00} s, {Size(new FileInfo(copy).Length)}) en la pista {session.Sequence.GetClip(session.SelectedClipIds[0]).Track.Name}.");

        // Exportar todo con el preajuste de emisión 1080i25 (ProRes entrelazado, EBU R128) de rodaje-preajustes-pro.
        IReadOnlyList<ExportPreset> presets = PackageExportPresets.Load(service.Installed);
        Assert.Equal(18, presets.Count);
        var content = new EffectContent(loader);
        var runner = new AppExportRunner(content, null, () => TitleFontFiles.Find(service.Installed));
        IReadOnlyList<Rodaje.Media.Export.EncoderSpec> encoders = await runner.GetEncodersAsync();
        Assert.All(presets, p => Assert.Empty(ExportSettingsValidator.Validate(p.Settings,
            p.Settings.Video is { } v ? ExportPlan.Choose(v.Codec, v.EncoderId, encoders) : null,
            p.Settings.Audio is { } a ? ExportPlan.Choose(a.Codec, a.EncoderId, encoders) : null)));
        ExportPreset broadcast = Assert.Single(presets, p => p.Id == "rodaje-preajustes-pro@1.0.0/emision-1080i25-ebu");
        string interlaced = Path.Combine(_folder.Path, "emisión 1080i25.mov");
        double seconds = await ExportAsync(runner, session.Project.Clone(), session.SequenceId, interlaced, broadcast.Settings);
        string probe = await ProbeAsync("-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,width,height,field_order,r_frame_rate,nb_read_frames", "-of", "compact", interlaced);
        Assert.Contains("codec_name=prores", probe, StringComparison.Ordinal);
        Assert.Contains("width=1920|height=1080", probe, StringComparison.Ordinal);
        Assert.Contains("field_order=tb", probe, StringComparison.Ordinal);
        Assert.Contains("r_frame_rate=25/1", probe, StringComparison.Ordinal);
        Note($"Exportado con «{broadcast.Name}»: {Size(new FileInfo(interlaced).Length)} en {seconds:0.0} s ({probe.Trim()}).");

        // Exportar en 360: la toma sin efectos en una secuencia 2:1, con el preajuste 360 de rodaje-360 y los metadatos esféricos.
        ExportPreset spherical = Assert.Single(presets, p => p.Id == "rodaje-360@1.0.0/360-4k-h264");
        var plain = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await plain.OpenFilesAsync([media!]);
        Assert.Equal((1920, 960), (plain.Sequence.Width, plain.Sequence.Height));
        string video360 = Path.Combine(_folder.Path, "toma 360.mp4");
        seconds = await ExportAsync(runner, plain.Project.Clone(), plain.SequenceId, video360, spherical.Settings);
        Assert.True(SphericalMetadata.IsEquirectangular(video360));
        string sphere = await ProbeAsync("-select_streams", "v:0", "-show_streams", video360);
        Assert.Contains("equirectangular", sphere, StringComparison.Ordinal);
        Assert.Contains("width=3840", sphere, StringComparison.Ordinal);
        Note($"Exportado con «{spherical.Name}»: {Size(new FileInfo(video360).Length)} en {seconds:0.0} s, 3840×1920 con metadatos esféricos.");

        foreach (string id in TodoElContenido)
        {
            await service.UninstallAsync(id);
        }

        Assert.DoesNotContain(service.Installed.Packages, p => p.Origin == PackageOrigin.Installed);
        EffectCatalog.EnabledFeatures = EffectCatalog.FeaturesFrom(service.Installed);
        Assert.DoesNotContain(EffectCatalog.For(TrackKind.Video), e => e.Type == BuiltInEffects.Reframe360);
        Assert.Empty(PackageExportPresets.Load(service.Installed));
        Note($"Desinstalados los {TodoElContenido.Length} complementos de contenido; el reencuadre 360 y los preajustes de paquete ya no se ofrecen.");
        _ = installed;
    }, TimeSpan.FromMinutes(20));

    /// <summary>
    /// Cada módulo de IA se instala desde el catálogo con sus modelos (Hugging Face y releases, unos 3,5 GB) y hace su función
    /// en un proyecto con voz real: subtítulos, traducción, texto a voz, reducción de ruido, quitar fondo, reencuadre y
    /// CinemaDNG. Después se desinstalan con sus procesos abiertos y el almacén de modelos queda vacío.
    /// </summary>
    [RedFact]
    public void Cada_modulo_de_ia_baja_sus_modelos_y_hace_su_funcion() => UiThread.Run(async () =>
    {
        Assert.True(AudioAiCorpus.Voice is not null, "Falta la voz de prueba de .entorno\\corpus (build\\preparar-modelos.ps1 -Corpus).");
        PackageService service = Service();
        var downloader = Assert.IsType<GitHubReleaseDownloader>(service.Downloader);
        SignedIndex index = await downloader.GetIndexAsync();
        await using ModuleManager modules = new(new ModuleManagerOptions { Roots = [new ModuleRoot(Locations.Modules, ModuleOrigin.Installed)] });
        service.CloseModule = modules.CloseModuleAsync;
        var failures = new List<ModuleFailure>();
        modules.Failed += (_, f) => failures.Add(f);
        // modulo-traducir tiene dos versiones: se instala la 1.0.0 desde su archivo y se actualiza a la 1.0.1 desde el catálogo.
        IndexEntry oldTranslator = index.Entries.Single(e => e.Id == "modulo-traducir" && e.Version.ToString() == "1.0.0");
        string oldPath = Path.Combine(_folder.Path, "modulo-traducir-1.0.0.paquete");
        await downloader.Files.DownloadAsync(new DownloadTarget(oldTranslator.Url, oldTranslator.Sha256, oldTranslator.Size), oldPath, null, default);
        Assert.Equal(InstallOutcome.Installed, (await service.InstallFromFileAsync(oldPath, oldTranslator.Sha256)).Outcome);
        foreach (string id in Modulos)
        {
            var watch = Stopwatch.StartNew();
            InstallResult result = await service.InstallFromCatalogAsync(service.Catalog.Find(id)!);
            if (id == "modulo-traducir")
            {
                Assert.Equal((InstallOutcome.Updated, "1.0.0", "1.0.1"), (result.Outcome, result.PreviousVersion?.ToString(), result.Package.Manifest.Version.ToString()));
            }

            Assert.Empty(service.MissingModelsOf(id));
            long models = ModuleModels.Read(Path.Combine(result.Package.Directory, PackageManifest.FileName)).Sum(m => m.Size);
            Note($"{id} {result.Package.Manifest.Version}{(result.PreviousVersion is { } before ? $" (actualizado desde {before})" : "")}: paquete {Size(Latest(index, id).Size)} + modelos {Size(models)} en {watch.Elapsed.TotalSeconds:0.0} s.");
        }

        modules.Refresh();
        Assert.Empty(modules.Problems);
        MatrixCase voiceCase = MatrixAiTests.Cases[0];
        (string? path, string? problem) = MatrixMedia.GetOrCreate(FfmpegTool.Default!, voiceCase);
        Assert.True(path is not null, problem);
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([path!]);
        Clip audio = session.Sequence.AudioTracks.SelectMany(t => t.Clips).First();
        Clip video = session.Sequence.VideoTracks.SelectMany(t => t.Clips).First();
        MediaRef media = session.Project.FindMedia(video.MediaId)!;

        Transcript transcript = await Timed("subtitulos", () => AutoSubtitles.RunAsync(modules, Require(modules, AutoSubtitles.Function), path!,
            AutoSubtitles.Window(audio), "es", "Subtítulos", CancellationToken.None), t =>
        {
            IReadOnlyList<Rodaje.Core.Editing.SpeechSegment> segments = AutoSubtitles.ToSequence(audio, t);
            int words = segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.True(words >= 10, $"Solo {words} palabras en 12 s de voz.");
            Assert.True(session.Execute(AutoSubtitles.Command(session.SequenceId, segments, SubtitleStyles.NewSubtitle())));
            return $"{segments.Count} subtítulos, {words} palabras: «{Shorten(segments[0].Text)}»";
        });

        await Timed("traducir", async () =>
        {
            Track track = SubtitleTrackTranslation.TranslatableTracks(session.Sequence)[0];
            return await new ModuleSubtitleTranslator(modules).TranslateAsync(SubtitleTrackTranslation.Collect(track), "es", "en", "Traducir", CancellationToken.None);
        }, r =>
        {
            Assert.Empty(r.Untranslated);
            return $"{r.Texts.Count} textos al inglés: «{Shorten(r.Texts.Values.First())}»";
        });

        await Timed("voz", () => new ModuleVoiceSynthesizer(modules).SynthesizeAsync(
            new VoiceSettings("Hola. Esta voz sale del módulo de texto a voz que se bajó del catálogo."), "Voz", CancellationToken.None), v =>
        {
            Assert.True(v.Samples.Length > v.SampleRate);
            return $"{v.Samples.Length / (double)v.SampleRate:0.0} s a {v.SampleRate} Hz";
        });

        await Timed("audio-ia", () => NoiseReduction.RunAsync(modules, Require(modules, NoiseReduction.Function), path!,
            NoiseReduction.Window(audio, media.Duration), _folder.Path, "Reducir ruido", CancellationToken.None), c =>
        {
            Assert.True(File.Exists(c.Path));
            return $"{c.Duration.ToSeconds():0.0} s sin ruido";
        });

        var shortClip = new Clip { Id = video.Id, MediaId = video.MediaId, Start = video.Start, SourceIn = video.SourceIn, SourceOut = video.SourceIn + Time.FromSeconds(2) };
        await Timed("vision (quitar fondo)", () => VisionTools.RemoveBackgroundAsync(modules, Require(modules, VisionTools.RemoveBackgroundFunction),
            FfmpegExecutable.Locate(), media.Path, shortClip, media.FrameRate!.Value, highQuality: false, _folder.Path, "Quitar fondo", CancellationToken.None), m =>
        {
            Assert.True(m.Frames > 0 && File.Exists(m.Path));
            return $"{m.Frames} cuadros de máscara {m.Width}×{m.Height}";
        });

        await Timed("vision (reencuadre)", () => VisionTools.ReframeAsync(modules, Require(modules, VisionTools.ReframeFunction), media.Path, shortClip,
            9 / 16.0, faces: false, 0.5, "Reencuadre", CancellationToken.None), p =>
        {
            Assert.NotEmpty(p.Keys);
            return $"{p.Frames} cuadros analizados, {p.Keys.Count} puntos";
        });

        string dngFolder = Path.Combine(_folder.Path, "Toma ñ");
        Directory.CreateDirectory(dngFolder);
        byte[] dng = (SyntheticDng.Chart(640, 360) with { FrameRate = (25, 1) }).Write();
        for (int i = 0; i < 25; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(dngFolder, string.Create(CultureInfo.InvariantCulture, $"Toma_{i:00000}.dng")), dng);
        }

        await Timed("raw (CinemaDNG)", () => CinemaDngImport.RunAsync(modules, Require(modules, CinemaDngImport.Function), FfmpegExecutable.Locate(),
            CinemaDngImport.Scan(dngFolder)!, new CinemaDngOptions(), Path.Combine(_folder.Path, "medios"), "CinemaDNG"), r =>
        {
            Assert.True(File.Exists(r.Path));
            return $"{r.Frames} cuadros {r.Width}×{r.Height} → ProRes 422 HQ";
        });

        Assert.Empty(failures.Select(f => f.Message));
        int open = modules.ProcessIds.Count;
        foreach (string id in Modulos)
        {
            await service.UninstallAsync(id);
        }

        Assert.Empty(Directory.Exists(service.Models.Folder) ? Directory.GetFiles(service.Models.Folder) : []);
        Note($"Desinstalados los {Modulos.Length} módulos ({open} con su proceso abierto); el almacén de modelos quedó vacío.");
    }, TimeSpan.FromMinutes(40));

    private async Task<T> Timed<T>(string step, Func<Task<T>> body, Func<T, string> check)
    {
        var watch = Stopwatch.StartNew();
        T result = await body();
        double seconds = watch.Elapsed.TotalSeconds;
        Note(string.Create(CultureInfo.InvariantCulture, $"{step}: {check(result)}; {seconds:0.0} s (incluye abrir el módulo)."));
        return result;
    }

    private static ModuleDescriptor Require(ModuleManager manager, string function) =>
        manager.FindByFunction(function) ?? throw new InvalidOperationException($"Ningún módulo instalado ofrece «{function}» ({string.Join(" ", manager.Problems)}).");

    private static async Task<double> ExportAsync(AppExportRunner runner, Project project, string sequenceId, string path, ExportSettings settings)
    {
        var watch = Stopwatch.StartNew();
        using IExportJob job = runner.Start(new ExportRequest(project, sequenceId, path, settings));
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(10));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        return watch.Elapsed.TotalSeconds;
    }

    private static async Task<string> ProbeAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(FfmpegExecutable.Locate().Path)!, "ffprobe.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in (string[])["-v", "error", .. args])
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string text = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return text;
    }

    /// <summary>La LUT en el eje neutro, interpolada entre los dos nodos grises vecinos.</summary>
    private static double Trilinear(LutTable lut, double input)
    {
        double x = Math.Clamp(input, 0, 1) * (lut.Size - 1);
        int i = Math.Min((int)x, lut.Size - 2);
        double f = x - i;
        double At(int k) => lut.Rgb[(((k * lut.Size * lut.Size) + (k * lut.Size) + k) * 3) + 1];
        return (At(i) * (1 - f)) + (At(i + 1) * f);
    }

    private static string Shorten(string text) => text.Length <= 50 ? text.Trim() : text[..50].Trim() + "…";
}
