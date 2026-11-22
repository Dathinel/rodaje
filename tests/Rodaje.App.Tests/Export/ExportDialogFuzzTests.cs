using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Export.Running;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Reporte de un PC ajeno: al elegir 60 fps o cambiar el tamaño en Exportar la app se cierra. Rehacer los elementos del
/// desplegable que se acaba de usar cierra la app desde WinUI (tarea 91), así que elegir algo en un desplegable nunca puede
/// cambiar la lista de ese mismo desplegable. Y lo que el diálogo deja exportar tiene que armar su plan sin lanzar, con
/// secuencias de tamaños y tasas raros y los codificadores de NVIDIA, AMD, Intel, Media Foundation o solo CPU.
/// </summary>
public class ExportDialogFuzzTests
{
    private static readonly (int Width, int Height)[] SequenceSizes =
        [(1080, 2400), (2400, 1080), (1366, 768), (2560, 1080), (3440, 1440), (1920, 1200), (853, 480), (1281, 721), (720, 1280), (7680, 4320), (16, 16)];

    // Las dos últimas, como las de un video de celular de tasa variable (la media que da el archivo).
    private static readonly Rational[] SequenceRates =
        [Rational.Ntsc24, Rational.Ntsc30, Rational.Fps30, Rational.Ntsc60, Rational.Fps60, new(120, 1), new(144, 1), new(240, 1), new(2997003, 100000), new(5994, 100)];

    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string machine in Machines.Keys)
        {
            foreach ((int w, int h) in SequenceSizes)
            {
                data.Add(machine, w, h);
            }
        }

        return data;
    }

    private static readonly Dictionary<string, IReadOnlyList<EncoderSpec>> Machines = new()
    {
        ["NVIDIA"] = SimulatedEncoders.All,
        ["AMD"] = Catalog(e => e.Vendor is not (EncoderVendor.Nvidia or EncoderVendor.Intel)),
        ["Intel"] = Catalog(e => e.Vendor is not (EncoderVendor.Nvidia or EncoderVendor.Amd)),
        ["Media Foundation"] = Catalog(e => !e.IsHardware),
        ["Solo CPU"] = Catalog(e => !e.IsHardware && e.Vendor != EncoderVendor.Microsoft),
    };

    private static IReadOnlyList<EncoderSpec> Catalog(Func<EncoderSpec, bool> keep) => [.. ExportEncoderCatalog.All.Where(keep)];

    private static ExportDialogModel Model(TempDirectory dir, Sequence sequence, IReadOnlyList<EncoderSpec> encoders, ExportPreferences? preferences = null) =>
        new(preferences ?? new ExportPreferences { Folder = dir.Path }, sequence, [], encoders, "Celular", null);

    private static Project Project(int width, int height, Rational rate)
    {
        Project project = ExportTestData.Project(width, height);
        project.Sequences[0].FrameRate = rate;
        return project;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ElegirUnaTasaOUnTamano_NoCambiaLaListaDeEseDesplegable(string machine, int width, int height)
    {
        using var dir = new TempDirectory();
        var failures = new List<string>();
        foreach (Rational rate in SequenceRates)
        {
            Sequence sequence = Project(width, height, rate).Sequences[0];
            ExportDialogModel model = Model(dir, sequence, Machines[machine]);
            foreach (string preset in model.Presets.Select(p => p.Id))
            {
                model.SelectPreset(preset);
                if (model.Settings.Video is null)
                {
                    continue;
                }

                for (int i = 0; i < ExportGeometry.FrameRates.Count; i++)
                {
                    IReadOnlyList<string> before = model.FrameRateLabels;
                    Rational? chosen = ExportGeometry.FrameRates[i].Rate;
                    model.Update(s => s with { Video = s.Video! with { FrameRate = chosen } });
                    if (!before.SequenceEqual(model.FrameRateLabels))
                    {
                        failures.Add($"{rate} fps, «{preset}»: elegir «{before[i]}» cambia la lista de tasas a [{string.Join(", ", model.FrameRateLabels)}]");
                    }
                }

                model.SelectPreset(preset);
                IReadOnlyList<SizeChoice> sizes = model.SizeChoices;
                foreach (SizeChoice size in sizes)
                {
                    IReadOnlyList<string> before = [.. model.SizeChoices.Select(z => z.Label)];
                    model.Update(s => s with { Video = s.Video! with { Width = size.Width, Height = size.Height } });
                    IReadOnlyList<string> after = [.. model.SizeChoices.Select(z => z.Label)];
                    if (!before.SequenceEqual(after))
                    {
                        failures.Add($"{rate} fps, «{preset}»: elegir «{size.Label}» cambia la lista de tamaños a [{string.Join(", ", after)}]");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} elecciones rehacen su propio desplegable en {machine} {width}×{height}:\n"
            + string.Join("\n", failures.Distinct().Take(12)));
    }

    [Fact]
    public void LoQueElCodificadorNoHace_LoDiceElDialogo_YElPlanTampocoLoDeja()
    {
        using var dir = new TempDirectory();
        Project slowMotion = Project(1920, 1080, new Rational(240, 1));
        ExportDialogModel model = Model(dir, slowMotion.Sequences[0], Machines["Media Foundation"]);
        Assert.Equal("h264_mf", model.VideoEncoder!.Id);
        string problem = Assert.Single(model.Problems);
        Assert.Contains("h264_mf codifica hasta 172 cuadros por segundo", problem, StringComparison.Ordinal);
        ExportRequest request = new(slowMotion, "sec", dir.File("lento.mp4"), model.ResolvedSettings());
        ExportException plan = Assert.Throws<ExportException>(() => ExportPlan.Create(request, model.Encoders));
        Assert.Equal(problem, plan.Message);

        model.Update(s => s with { Video = s.Video! with { FrameRate = Rational.Fps60 } });
        Assert.Empty(model.Problems);

        Project eightK = Project(7680, 4320, Rational.Fps30);
        model = Model(dir, eightK.Sequences[0], Machines["AMD"]);
        model.Update(s => s with { Video = s.Video! with { Width = null, Height = null } });
        Assert.Equal("h264_amf", model.VideoEncoder!.Id);
        Assert.Contains("h264_amf codifica hasta 4096 px por lado y la salida sería de 7680×4320", Assert.Single(model.Problems), StringComparison.Ordinal);
        model.Update(s => s with { Video = ExportSettingsEditor.DefaultVideo(ExportCodec.Hevc, model.Encoders) });
        Assert.Equal("hevc_amf", model.VideoEncoder!.Id);
        Assert.Empty(model.Problems);
    }

    /// <summary>
    /// Exportar a 30, cancelar, volver a abrir el diálogo (con lo recordado), cambiar tasa y tamaño y exportar otra vez a la misma
    /// salida, y repetir desde la tarjeta: nada lanza y la última termina.
    /// </summary>
    [Fact]
    public void ExportarCancelarCambiarYExportarOtraVez_EnUnaSecuenciaDeCelular() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        Project project = Project(1080, 2400, new Rational(2997003, 100000));
        var preferences = new ExportPreferences { Folder = dir.Path };
        using var list = new ExportJobList(new SimulatedExportRunner { FramesPerSecond = 300, Step = TimeSpan.FromMilliseconds(5) }, SynchronizationContext.Current);
        ExportEntry? last = null;
        foreach (Rational? rate in new Rational?[] { Rational.Fps30, Rational.Fps30, Rational.Fps60, null })
        {
            ExportDialogModel model = Model(dir, project.Sequences[0], SimulatedEncoders.All, preferences);
            model.Update(s => s with { Video = s.Video! with { FrameRate = rate } });
            model.Update(s => s with { Video = s.Video! with { Width = model.SizeChoices[^1].Width, Height = model.SizeChoices[^1].Height } });
            Assert.True(model.CanExport, string.Join(" ", model.Problems));
            last = list.Enqueue(model.CreateRequest(project.Clone(), "sec", dir.Path));
            await Task.Delay(15);
            list.Cancel(last);
            await WaitAsync(() => last.IsFinished);
            Assert.Equal(ExportJobState.Canceled, last.Progress.State);
        }

        ExportEntry again = list.Retry(last!, current: project);
        await WaitAsync(() => again.IsFinished);
        Assert.Equal(ExportJobState.Completed, again.Progress.State);
    });

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "No llegó al estado esperado en 10 s.");
    }

    /// <summary>
    /// Cambios al azar (semilla fija por caso) como los haría la persona en el diálogo, con lo que la vista lee en cada
    /// refresco; si el diálogo deja exportar, el plan y la línea de ffmpeg salen sin lanzar.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void CambiosAlAzar_NoLanzan_YLoQueDejaExportarArmaSuPlan(string machine, int width, int height)
    {
        using var dir = new TempDirectory();
        IReadOnlyList<EncoderSpec> encoders = Machines[machine];
        var random = new Random(HashCode.Combine(machine.Length, width, height) & 0x7fffffff);
        foreach (Rational rate in SequenceRates)
        {
            Project project = Project(width, height, rate);
            ExportDialogModel model = Model(dir, project.Sequences[0], encoders);
            var steps = new List<string>();
            for (int step = 0; step < 40; step++)
            {
                steps.Add(DialogDriver.RandomChange(model, random));
                try
                {
                    DialogDriver.ReadLikeTheView(model);
                    if (model.CanExport)
                    {
                        ExportRequest request = model.CreatePreviewRequest(project.Clone(), "sec", dir.Path);
                        ExportPlan plan = ExportPlan.Create(request, encoders);
                        _ = ExportCommandLine.Text(plan);
                        (int w, int h) = model.OutputSize;
                        Assert.Equal((w, h), (plan.Video?.Width ?? w, plan.Video?.Height ?? h));
                    }
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    Assert.Fail($"{machine} {width}×{height} a {rate} fps, tras [{string.Join(" → ", steps)}]: {ex}");
                }
            }
        }
    }
}
