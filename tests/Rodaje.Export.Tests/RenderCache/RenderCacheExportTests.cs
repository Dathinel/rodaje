using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Running;
using Rodaje.Media;
using Rodaje.Media.Export;
using Rodaje.Playback.Composition;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.RenderCache;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.RenderCache;

[Collection(ExportGpuGroup.Name)]
public sealed partial class RenderCacheExportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tempFolder = Directory.CreateTempSubdirectory("rodaje-export-cache-").FullName;

    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> Encoders = new(() =>
        new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [ProxyFact]
    public async Task Exportar_con_render_en_cache_cumple_psnr_y_tarda_menos()
    {
        Project project = ExportProjects.TwoTracks();
        string cacheDir = Path.Combine(_tempFolder, "almacen");
        var store = new RenderCacheStore(cacheDir);
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;

        var proresHq = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes,
                Profile = "hq",
                BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma422,
                RateControl = RateControl.ProfileDefined,
            },
            Audio = null,
            FastStart = false,
        };

        var range = new ExportRange(ExportProjects.S(1), ExportProjects.S(2));

        // Paso 1: Crear el fragmento pre-renderizado con el propio exportador ProRes HQ
        string tempFragmentPath = store.CreateTemporaryPath();
        var runnerGen = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
        });

        ExportJob jobGen = runnerGen.Start(new ExportRequest(project, "sec", tempFragmentPath, proresHq, range), encoders);
        await jobGen.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ExportJobState.Completed, jobGen.Progress.State);
        Assert.True(File.Exists(tempFragmentPath));

        // Paso 2: Construir recetas y publicar el fragmento en el almacén con Commit
        RecipeBuilder recipes = CreateRecipeBuilder(project, "sec");
        var keys = new List<RenderCacheKey>();
        for (long f = 30; f < 60; f++)
        {
            RenderCacheKey key = recipes.KeyAt(f) ?? throw new InvalidOperationException($"Sin receta para el cuadro {f}");
            keys.Add(key);
        }

        var tempInfo = new FileInfo(tempFragmentPath);
        var metadata = new RenderCacheFragmentMetadata(
            Width: 1920,
            Height: 1080,
            FrameRate: Rational.Fps30,
            ColorSpace: SequenceColorSpace.Sdr709,
            Codec: "prores_hq",
            PlaybackOnly: false,
            Frames: keys,
            MovieBytes: tempInfo.Length);

        store.Commit(tempFragmentPath, metadata);
        Assert.Equal(30, store.Count);

        // Paso 3: Exportar de cero (UseRenderCache = false), midiendo tiempo
        string scratchPath = Path.Combine(_tempFolder, "scratch.mov");
        var scratchSettings = proresHq with { UseRenderCache = false };
        var runnerScratch = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
            RenderCache = store,
            RecipeBuilder = r => CreateRecipeBuilder(r.Project, r.SequenceId),
        });

        var swScratch = Stopwatch.StartNew();
        ExportJob jobScratch = runnerScratch.Start(new ExportRequest(project, "sec", scratchPath, scratchSettings, range), encoders);
        await jobScratch.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        swScratch.Stop();

        Assert.Equal(ExportJobState.Completed, jobScratch.Progress.State);
        Assert.Equal(0, jobScratch.CachedFrames);
        Assert.True(File.Exists(scratchPath));

        // Paso 4: Exportar con la casilla (UseRenderCache = true), midiendo tiempo
        string cachePath = Path.Combine(_tempFolder, "cache.mov");
        var cacheSettings = proresHq with { UseRenderCache = true };
        var runnerCache = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
            RenderCache = store,
            RecipeBuilder = r => CreateRecipeBuilder(r.Project, r.SequenceId),
        });

        var swCache = Stopwatch.StartNew();
        ExportJob jobCache = runnerCache.Start(new ExportRequest(project, "sec", cachePath, cacheSettings, range), encoders);
        await jobCache.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        swCache.Stop();

        Assert.Equal(ExportJobState.Completed, jobCache.Progress.State);
        Assert.Equal(30, jobCache.CachedFrames);
        Assert.True(File.Exists(cachePath));

        // Medir PSNR entre la exportación con caché y la exportación de cero con el filtro nativo de FFmpeg
        (double psnrAvg, double psnrMin) = await MeasureFfmpegPsnrAsync(scratchPath, cachePath);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Exportación de cero: {swScratch.Elapsed.TotalMilliseconds:F1} ms; con caché: {swCache.Elapsed.TotalMilliseconds:F1} ms; PSNR medio: {psnrAvg:F2} dB, peor: {psnrMin:F2} dB."));

        Assert.True(psnrAvg >= 45.0, string.Create(CultureInfo.InvariantCulture, $"PSNR {psnrAvg:F2} dB es inferior a los 45 dB exigidos."));
        Assert.True(swCache.Elapsed <= swScratch.Elapsed,
            $"Exportar con caché ({swCache.ElapsedMilliseconds} ms) debe tardar menos o igual que de cero ({swScratch.ElapsedMilliseconds} ms).");
    }

    [ProxyFact]
    public async Task Cuadros_fuera_de_rejilla_entrelazados_y_con_alfa_se_componen_siempre()
    {
        Project project = ExportProjects.TwoTracks();
        string cacheDir = Path.Combine(_tempFolder, "almacen_condiciones");
        var store = new RenderCacheStore(cacheDir);
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;

        var proresHq = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes,
                Profile = "hq",
                BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma422,
                RateControl = RateControl.ProfileDefined,
            },
            Audio = null,
            FastStart = false,
        };

        var range = new ExportRange(ExportProjects.S(1), ExportProjects.S(2));

        // Preparar fragmento en el almacén
        string tempFragmentPath = store.CreateTemporaryPath();
        var runnerGen = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
        });
        ExportJob jobGen = runnerGen.Start(new ExportRequest(project, "sec", tempFragmentPath, proresHq, range), encoders);
        await jobGen.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        RecipeBuilder recipes = CreateRecipeBuilder(project, "sec");
        var keys = new List<RenderCacheKey>();
        for (long f = 30; f < 60; f++)
        {
            keys.Add(recipes.KeyAt(f)!.Value);
        }

        store.Commit(tempFragmentPath, new RenderCacheFragmentMetadata(
            Width: 1920, Height: 1080, FrameRate: Rational.Fps30, ColorSpace: SequenceColorSpace.Sdr709,
            Codec: "prores_hq", PlaybackOnly: false, Frames: keys, MovieBytes: new FileInfo(tempFragmentPath).Length));

        var runner = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
            RenderCache = store,
            RecipeBuilder = r => CreateRecipeBuilder(r.Project, r.SequenceId),
        });

        // 1. Cuadros fuera de la rejilla (conversión de tasa a 24 fps)
        string offGridPath = Path.Combine(_tempFolder, "offgrid.mov");
        var offGridSettings = proresHq with
        {
            UseRenderCache = true,
            Video = proresHq.Video! with { FrameRate = Rational.Fps24 },
        };
        ExportJob jobOffGrid = runner.Start(new ExportRequest(project, "sec", offGridPath, offGridSettings, range), encoders);
        await jobOffGrid.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ExportJobState.Completed, jobOffGrid.Progress.State);
        Assert.Equal(0, jobOffGrid.CachedFrames);

        // 2. Entrelazados: una secuencia de 60 fps sale entrelazada a 30; la tasa de campos es la de la
        // secuencia, así que no hay conversión de tasa y lo único que excluye el caché es el entrelazado.
        Project interlacedProject = ExportProjects.TwoTracks();
        interlacedProject.Sequences[0].FrameRate = Rational.Fps60;
        string interlacedFragmentPath = store.CreateTemporaryPath();
        ExportJob jobGen60 = runnerGen.Start(new ExportRequest(interlacedProject, "sec", interlacedFragmentPath, proresHq, range), encoders);
        await jobGen60.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(jobGen60.Progress.State == ExportJobState.Completed, jobGen60.Failure?.ToString());

        RecipeBuilder recipes60 = CreateRecipeBuilder(interlacedProject, "sec");
        var keys60 = new List<RenderCacheKey>();
        for (long f = 60; f < 120; f++)
        {
            keys60.Add(recipes60.KeyAt(f)!.Value);
        }

        store.Commit(interlacedFragmentPath, new RenderCacheFragmentMetadata(
            Width: 1920, Height: 1080, FrameRate: Rational.Fps60, ColorSpace: SequenceColorSpace.Sdr709,
            Codec: "prores_hq", PlaybackOnly: false, Frames: keys60, MovieBytes: new FileInfo(interlacedFragmentPath).Length));

        string interlacedPath = Path.Combine(_tempFolder, "interlaced.mov");
        var interlacedSettings = proresHq with
        {
            UseRenderCache = true,
            Video = proresHq.Video! with { FrameRate = Rational.Fps30, FieldOrder = ExportFieldOrder.TopFirst },
        };
        ExportJob jobInterlaced = runner.Start(new ExportRequest(interlacedProject, "sec", interlacedPath, interlacedSettings, range), encoders);
        await jobInterlaced.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(jobInterlaced.Progress.State == ExportJobState.Completed, jobInterlaced.Failure?.ToString());
        Assert.Equal(0, jobInterlaced.CachedFrames);

        // 3. Con alfa (ProRes 4444 con canal alfa)
        string alphaPath = Path.Combine(_tempFolder, "alpha.mov");
        var alphaSettings = proresHq with
        {
            UseRenderCache = true,
            Video = proresHq.Video! with { Codec = ExportCodec.ProRes, Profile = "4444", Alpha = true, Chroma = ChromaSubsampling.Chroma444 },
        };
        ExportJob jobAlpha = runner.Start(new ExportRequest(project, "sec", alphaPath, alphaSettings, range), encoders);
        await jobAlpha.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(jobAlpha.Progress.State == ExportJobState.Completed, jobAlpha.Failure?.ToString());
        Assert.Equal(0, jobAlpha.CachedFrames);
    }

    [ProxyFact]
    public async Task Con_subtitulos_aparte_las_huellas_salen_sin_ellos_en_la_imagen()
    {
        Project project = ExportProjects.TwoTracks();
        var subtitles = new Track { Id = "subs", Kind = TrackKind.Video, Name = "Español", Role = TrackRoles.Subtitles };
        subtitles.Clips.Add(new Clip
        {
            Id = "s1", Start = ExportProjects.S(1), SourceIn = Time.Zero, SourceOut = ExportProjects.S(1),
            Generator = new ClipGenerator { Text = "Hola", Style = new TextStyle() },
        });
        project.Sequences[0].VideoTracks.Add(subtitles);
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Subtitles = SubtitleExport.Embedded,
            UseRenderCache = true,
            Video = new VideoExportSettings { Codec = ExportCodec.ProRes, Profile = "hq", BitDepth = 10, Chroma = ChromaSubsampling.Chroma422, RateControl = RateControl.ProfileDefined },
            Audio = null,
            FastStart = false,
        };

        Project? seen = null;
        var runner = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
            RenderCache = new RenderCacheStore(Path.Combine(_tempFolder, "almacen_subtitulos")),
            RecipeBuilder = r =>
            {
                seen = r.Project;
                return CreateRecipeBuilder(r.Project, r.SequenceId);
            },
        });

        var range = new ExportRange(ExportProjects.S(1), ExportProjects.S(1.2));
        ExportJob job = runner.Start(new ExportRequest(project, "sec", Path.Combine(_tempFolder, "subs.mov"), settings, range), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        Assert.NotNull(seen);
        Assert.True(seen.FindSequence("sec")!.VideoTracks.Single(t => t.Role == TrackRoles.Subtitles).Hidden);
    }

    [ProxyFact]
    public async Task Sin_casilla_no_lee_cache_y_fragmento_borrado_a_mitad_se_olvida()
    {
        Project project = ExportProjects.TwoTracks();
        string cacheDir = Path.Combine(_tempFolder, "almacen_borrado");
        var store = new RenderCacheStore(cacheDir);
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;

        var proresHq = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes,
                Profile = "hq",
                BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma422,
                RateControl = RateControl.ProfileDefined,
            },
            Audio = null,
            FastStart = false,
        };

        // Crear dos fragmentos: tramo 1 (cuadros 30..44) y tramo 2 (cuadros 45..59)
        var range1 = new ExportRange(ExportProjects.S(1.0), ExportProjects.S(1.5));
        var range2 = new ExportRange(ExportProjects.S(1.5), ExportProjects.S(2.0));
        var rangeFull = new ExportRange(ExportProjects.S(1.0), ExportProjects.S(2.0));

        var runnerGen = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
        });

        string temp1 = store.CreateTemporaryPath();
        ExportJob jobGen1 = runnerGen.Start(new ExportRequest(project, "sec", temp1, proresHq, range1), encoders);
        await jobGen1.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        string temp2 = store.CreateTemporaryPath();
        ExportJob jobGen2 = runnerGen.Start(new ExportRequest(project, "sec", temp2, proresHq, range2), encoders);
        await jobGen2.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        RecipeBuilder recipes = CreateRecipeBuilder(project, "sec");
        var keys1 = new List<RenderCacheKey>();
        for (long f = 30; f < 45; f++) keys1.Add(recipes.KeyAt(f)!.Value);
        var keys2 = new List<RenderCacheKey>();
        for (long f = 45; f < 60; f++) keys2.Add(recipes.KeyAt(f)!.Value);

        RenderCacheFragment frag1 = store.Commit(temp1, new RenderCacheFragmentMetadata(
            Width: 1920, Height: 1080, FrameRate: Rational.Fps30, ColorSpace: SequenceColorSpace.Sdr709,
            Codec: "prores_hq", PlaybackOnly: false, Frames: keys1, MovieBytes: new FileInfo(temp1).Length));

        RenderCacheFragment frag2 = store.Commit(temp2, new RenderCacheFragmentMetadata(
            Width: 1920, Height: 1080, FrameRate: Rational.Fps30, ColorSpace: SequenceColorSpace.Sdr709,
            Codec: "prores_hq", PlaybackOnly: false, Frames: keys2, MovieBytes: new FileInfo(temp2).Length));

        Assert.Equal(30, store.Count);

        var runner = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Skip.ProxyExecutable!,
            CreateDevice = () => GpuDevice.CreateWarp(),
            RenderCache = store,
            RecipeBuilder = r => CreateRecipeBuilder(r.Project, r.SequenceId),
        });

        // Caso 1: Sin la casilla nunca se lee el caché
        string pathNoCache = Path.Combine(_tempFolder, "sin_casilla.mov");
        ExportJob jobNoCache = runner.Start(new ExportRequest(project, "sec", pathNoCache, proresHq with { UseRenderCache = false }, rangeFull), encoders);
        await jobNoCache.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ExportJobState.Completed, jobNoCache.Progress.State);
        Assert.Equal(0, jobNoCache.CachedFrames);

        // Caso 2: Se borra el fragmento 2 en disco; el fragmento 1 se lee del caché (15 cuadros),
        // el fragmento 2 falla al abrirse, se olvida (Forget) y los cuadros 45..59 se componen en vivo.
        File.Delete(frag2.MoviePath);

        string pathDeleted = Path.Combine(_tempFolder, "borrado_mitad.mov");
        ExportJob jobDeleted = runner.Start(new ExportRequest(project, "sec", pathDeleted, proresHq with { UseRenderCache = true }, rangeFull), encoders);
        await jobDeleted.Completion.WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(ExportJobState.Completed, jobDeleted.Progress.State);
        Assert.Equal(15, jobDeleted.CachedFrames);
        Assert.True(File.Exists(pathDeleted));

        // frag2 fue olvidado del almacén
        Assert.False(store.TryGet(keys2[0], null, out _));
        // frag1 sigue en el almacén
        Assert.True(store.TryGet(keys1[0], null, out _));
    }

    [Fact]
    public void Preajustes_y_ajustes_sin_propiedad_se_leen_con_false()
    {
        // Preajuste sin la propiedad
        const string presetJsonWithout = """
        {
          "id": "test.preset",
          "name": "Prueba",
          "description": "Sin cache",
          "category": "web",
          "settings": {
            "container": "mov",
            "video": {
              "codec": "prores"
            }
          }
        }
        """;
        ExportPreset? presetWithout = JsonSerializer.Deserialize(presetJsonWithout, RenderCacheExportJsonContext.Default.ExportPreset);
        Assert.NotNull(presetWithout);
        Assert.False(presetWithout.Settings.UseRenderCache);

        // Preajuste con UseRenderCache = true
        const string presetJsonWith = """
        {
          "id": "test.preset",
          "name": "Prueba",
          "description": "Con cache",
          "category": "web",
          "settings": {
            "container": "mov",
            "useRenderCache": true,
            "video": {
              "codec": "prores"
            }
          }
        }
        """;
        ExportPreset? presetWith = JsonSerializer.Deserialize(presetJsonWith, RenderCacheExportJsonContext.Default.ExportPreset);
        Assert.NotNull(presetWith);
        Assert.True(presetWith.Settings.UseRenderCache);

        // ExportSettings directo sin la propiedad
        const string settingsJsonWithout = """
        {
          "container": "mp4",
          "video": {
            "codec": "h264"
          }
        }
        """;
        ExportSettings? settingsWithout = JsonSerializer.Deserialize(settingsJsonWithout, RenderCacheExportJsonContext.Default.ExportSettings);
        Assert.NotNull(settingsWithout);
        Assert.False(settingsWithout.UseRenderCache);

        // ExportSettings directo con UseRenderCache = true
        const string settingsJsonWith = """
        {
          "container": "mp4",
          "useRenderCache": true,
          "video": {
            "codec": "h264"
          }
        }
        """;
        ExportSettings? settingsWith = JsonSerializer.Deserialize(settingsJsonWith, RenderCacheExportJsonContext.Default.ExportSettings);
        Assert.NotNull(settingsWith);
        Assert.True(settingsWith.UseRenderCache);
    }

    private static RecipeBuilder CreateRecipeBuilder(Project project, string sequenceId)
    {
        var mediaCache = new Dictionary<string, RecipeMedia?>(StringComparer.OrdinalIgnoreCase);
        return RecipeBuilder.Create(project, sequenceId, new RecipeBuilderOptions
        {
            Media = path =>
            {
                if (mediaCache.TryGetValue(path, out RecipeMedia? cached)) return cached;
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) { mediaCache[path] = null; return null; }
                string? hash = ContentFingerprint.Of(full);
                if (hash is null) { mediaCache[path] = null; return null; }
                VideoStreamInfo? video = MediaProbe.Probe(full).Video;
                if (video is null) { mediaCache[path] = null; return null; }
                string format = $"{video.PixelFormat}:{video.BitDepth}:{video.Color.Matrix}:{video.Color.FullRange}:{video.Width}x{video.Height}";
                var media = new RecipeMedia(new RecipeSource(hash, format), video.SampleAspectRatio, video.Rotation);
                mediaCache[path] = media;
                return media;
            },
            Resources = _ => null,
            Fonts = _ => null,
        });
    }

    private static async Task<(double Average, double Min)> MeasureFfmpegPsnrAsync(string reference, string distorted)
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        var info = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in (string[])[
            "-hide_banner", "-i", distorted, "-i", reference,
            "-lavfi", "psnr", "-f", "null", "-"])
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        double average = ParseMetric(error, "average:");
        double min = ParseMetric(error, "min:");
        return (average, min);
    }

    private static double ParseMetric(string text, string label)
    {
        int idx = text.IndexOf(label, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int start = idx + label.Length;
            int end = text.IndexOf(' ', start);
            if (end < 0) end = text.IndexOf('\r', start);
            if (end < 0) end = text.IndexOf('\n', start);
            if (end > start && double.TryParse(text[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
        }

        return 0;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    Converters = [typeof(RationalJsonConverter), typeof(TimeJsonConverter)])]
[JsonSerializable(typeof(ExportPreset))]
[JsonSerializable(typeof(ExportSettings))]
internal sealed partial class RenderCacheExportJsonContext : JsonSerializerContext
{
}
