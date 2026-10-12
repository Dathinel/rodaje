using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Effects;

/// <summary>
/// Los paquetes de contenido reales (<c>..\Complementos\rodaje-filtros</c> y <c>rodaje-transiciones</c>), empaquetados
/// con su <c>empaquetar.ps1</c>, instalados en un perfil temporal y aplicados desde la biblioteca como lo hace la app;
/// el resultado se compone con FFmpeg, D3D11VA y el render real y se comprueba leyendo los píxeles.
/// </summary>
public sealed class PackageContentEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string Filters = "rodaje-filtros";
    private const string Transitions = "rodaje-transiciones";
    private const int Width = 640;
    private const int Height = 360;
    private static readonly MediaSpec Spec = new("contenido-640-30", Width, Height, Rational.Fps30, TimeSpan.FromSeconds(8)) { GopLength = 30, BFrames = 2 };

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [ContentFact]
    public void Un_look_y_una_transicion_de_paquete_se_ven_y_al_desinstalar_el_proyecto_abre_con_aviso()
    {
        string media = TestMediaGenerator.CreateDefault()!.GetOrCreate(Spec).Path;
        string[] archives = [Pack(Filters), Pack(Transitions)];
        string projectPath = Path.Combine(_folder.Path, "proyecto.rodaje");
        ProjectSerializer.Save(BuildProject(media), projectPath);

        UiThread.Run(async () =>
        {
            var packages = new PackageService(PackageLocations.Under(Path.Combine(_folder.Path, "datos", "complementos")), PackageService.LoadBundledCatalog());
            foreach (string archive in archives)
            {
                Assert.Equal(InstallOutcome.Installed, (await packages.InstallFromFileAsync(archive)).Outcome);
            }

            var notices = new List<EditorNotification>();
            var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
            session.Notified += (_, n) => notices.Add(n);
            MissingContent.Attach(session, packages);
            Assert.True(await session.OpenProjectAsync(projectPath));
            Project plain = session.Project.Clone();

            // Como en la biblioteca: un look por clip y, en el corte, el deslizamiento hacia la izquierda.
            var applier = new ElementApplier(session, new InstalledContentLoader(() => packages.Installed));
            IReadOnlyList<LibraryElement> looks = LibraryElements.For(LibraryCategory.Filters, packages.Installed);
            Assert.DoesNotContain(looks, e => e.Kind == ItemKind.Lut && e.Source == "Filtros");
            session.Select(["a"]);
            Assert.True(await applier.ApplyAsync(looks.Single(e => e.Name == "Cálido")));
            session.Select(["b"]);
            Assert.True(await applier.ApplyAsync(looks.Single(e => e.Name == "Blanco y negro")));
            LibraryElement slide = LibraryElements.For(LibraryCategory.Transitions, packages.Installed).Single(e => e.Name == "Deslizar a la izquierda");
            Assert.True(await applier.ApplyAsync(slide));
            Assert.True(await session.SaveAsync());
            Assert.DoesNotContain(notices, n => n.Severity == NotificationSeverity.Warning);

            EffectContent content = AppEffectContent.Create(packages);
            string warmLut = session.Sequence.FindClip("a")!.Value.Clip.Effects.Single(e => e.Type == BuiltInEffects.Color).Options[BuiltInEffects.ColorOptions.Lut];
            LutTable warm = new InstalledContentLoader(() => packages.Installed).LoadLut(warmLut)!;
            using GpuDevice gpu = GpuDevice.Create();

            // El look: lo compuesto es la LUT aplicada en la CPU al mismo cuadro sin look.
            byte[] reference = Render(gpu, plain, 1, null, out _, out _);
            byte[] graded = Render(gpu, session.Project, 1, content, out CompositeResult result, out int missingTransitions);
            Assert.Equal((0, 0), (result.MissingContent, missingTransitions));
            Assert.True(result.EffectsApplied > 0);
            double lutError = MeanDifference(graded, ApplyLut(reference, warm));
            double lutChange = MeanDifference(graded, reference);
            output.WriteLine($"Cálido: {lutError * 255:0.00}/255 de la LUT en la CPU; cambia el cuadro {lutChange * 255:0.0}/255 de media.");
            Assert.True(lutError <= 2.0 / 255, $"La LUT se aleja {lutError * 255:0.00}/255 de la referencia.");
            Assert.True(lutChange >= 4.0 / 255, "El look no cambió el cuadro.");

            // En el corte, a mitad del barrido de 180°: a la izquierda el clip que sale (en color), a la derecha el que
            // entra (en blanco y negro). Sin los parámetros de la mezcla del paquete el barrido iría al revés.
            byte[] cut = Render(gpu, session.Project, 3, content, out result, out missingTransitions);
            Assert.Equal(0, missingTransitions);
            double left = Saturation(cut, 0.05, 0.40);
            double right = Saturation(cut, 0.60, 0.95);
            output.WriteLine($"En el corte: saturación {left * 255:0.0}/255 a la izquierda, {right * 255:0.0}/255 a la derecha.");
            Assert.True(left >= 8.0 / 255, "A la izquierda debía verse el clip que sale, en color.");
            Assert.True(right <= 1.5 / 255, "A la derecha debía verse el clip que entra, en blanco y negro.");

            // Sin los paquetes: el proyecto abre con aviso, sin LUT y con la transición como fundido cruzado.
            await packages.UninstallAsync(Filters);
            await packages.UninstallAsync(Transitions);
            notices.Clear();
            Assert.True(await session.OpenProjectAsync(projectPath));
            EditorNotification notice = Assert.Single(notices, n => n.Severity == NotificationSeverity.Warning);
            Assert.Contains(Filters, notice.Message, StringComparison.Ordinal);
            Assert.Contains(Transitions, notice.Message, StringComparison.Ordinal);
            byte[] missing = Render(gpu, session.Project, 1, content, out _, out missingTransitions);
            Assert.Equal(1, missingTransitions);
            Assert.True(MeanDifference(missing, reference) <= 0.5 / 255, "Sin el paquete, el clip debía verse sin LUT.");
        }, TimeSpan.FromMinutes(2));
    }

    private static Project BuildProject(string mediaPath)
    {
        var media = new MediaRef { Id = "m", Path = mediaPath };
        MediaProbe.Probe(mediaPath).ApplyTo(media);
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new Clip { Id = "a", MediaId = "m", Start = Time.Zero, SourceIn = Time.FromSeconds(1), SourceOut = Time.FromSeconds(4) });
        track.Clips.Add(new Clip { Id = "b", MediaId = "m", Start = Time.FromSeconds(3), SourceIn = Time.FromSeconds(4), SourceOut = Time.FromSeconds(7) });
        var sequence = new Sequence { Id = "sec", Name = "Contenido", FrameRate = Rational.Fps30, Width = Width, Height = Height };
        sequence.VideoTracks.Add(track);
        var project = new Project { Id = "p", Name = "Contenido", Sequences = [sequence] };
        project.Media.Add(media);
        return project;
    }

    /// <summary>
    /// Compone el instante con el motor y el render reales, tras compilar los shaders y cargar las LUT (en la app, el
    /// aviso <c>Changed</c> hace que se vuelva a componer). <paramref name="missing"/>: transiciones sin su paquete.
    /// </summary>
    private static byte[] Render(GpuDevice gpu, Project project, double seconds, EffectContent? content, out CompositeResult result, out int missing)
    {
        if (content is not null)
        {
            foreach (EffectInstance effect in project.Sequences[0].AllTracks.SelectMany(t => t.Clips).SelectMany(c => c.Effects))
            {
                if (effect.Options.GetValueOrDefault(BuiltInEffects.ColorOptions.Lut) is { Length: > 0 } lut)
                {
                    content.TryGetLut(new ResourceKey(lut), out _);
                    Assert.True(content.WhenLoadedAsync(lut).Wait(TimeSpan.FromSeconds(10)), $"La LUT {lut} no cargó en 10 s.");
                }
            }
        }

        using TimelineSession session = TimelineSession.Open(project, project.Sequences[0].Id, gpu,
            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never, Content = content });
        using var compositor = new Compositor(gpu, Width, Height) { Resources = content };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)), "Los shaders no compilaron en 60 s.");
        session.Seek(Time.FromSeconds(seconds));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            using CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                result = compositor.Render(frame);
                missing = session.Stats.MissingContent;
                return compositor.ReadbackRgba();
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Las capas no llegaron a su cuadro en 10 s.");
            Thread.Sleep(5);
        }
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        }

        return sum / (a.Length / 4 * 3.0 * 255);
    }

    /// <summary>Saturación media (máximo menos mínimo de RGB) entre dos fracciones del ancho, sin el borde de arriba y abajo.</summary>
    private static double Saturation(byte[] rgba, double from, double to)
    {
        long sum = 0;
        long count = 0;
        for (int y = Height / 10; y < Height * 9 / 10; y++)
        {
            for (int x = (int)(Width * from); x < (int)(Width * to); x++)
            {
                int i = ((y * Width) + x) * 4;
                sum += Math.Max(rgba[i], Math.Max(rgba[i + 1], rgba[i + 2])) - Math.Min(rgba[i], Math.Min(rgba[i + 1], rgba[i + 2]));
                count++;
            }
        }

        return sum / (count * 255.0);
    }

    /// <summary>LUT 3D con interpolación trilineal, como la del hardware.</summary>
    private static byte[] ApplyLut(byte[] rgba, LutTable lut)
    {
        byte[] result = (byte[])rgba.Clone();
        int n = lut.Size;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float r = rgba[i] / 255f * (n - 1);
            float g = rgba[i + 1] / 255f * (n - 1);
            float b = rgba[i + 2] / 255f * (n - 1);
            int r0 = Math.Min((int)r, n - 2);
            int g0 = Math.Min((int)g, n - 2);
            int b0 = Math.Min((int)b, n - 2);
            float fr = r - r0;
            float fg = g - g0;
            float fb = b - b0;
            for (int c = 0; c < 3; c++)
            {
                float At(int dr, int dg, int db) => lut.Rgb[((((b0 + db) * n) + g0 + dg) * n + r0 + dr) * 3 + c];
                float x00 = At(0, 0, 0) + ((At(1, 0, 0) - At(0, 0, 0)) * fr);
                float x10 = At(0, 1, 0) + ((At(1, 1, 0) - At(0, 1, 0)) * fr);
                float x01 = At(0, 0, 1) + ((At(1, 0, 1) - At(0, 0, 1)) * fr);
                float x11 = At(0, 1, 1) + ((At(1, 1, 1) - At(0, 1, 1)) * fr);
                float y0 = x00 + ((x10 - x00) * fg);
                float y1 = x01 + ((x11 - x01) * fg);
                result[i + c] = (byte)Math.Clamp(MathF.Round((y0 + ((y1 - y0) * fb)) * 255), 0, 255);
            }
        }

        return result;
    }

    /// <summary>Arma el <c>.paquete</c> con el <c>empaquetar.ps1</c> del repositorio de contenido, en una carpeta temporal.</summary>
    private string Pack(string id)
    {
        string script = Path.Combine(ContentFactAttribute.ContentRoot!, id, "empaquetar.ps1");
        string destination = Path.Combine(_folder.Path, "dist");
        using var process = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Salida", destination])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        string log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000) && process.ExitCode == 0, $"empaquetar.ps1 de {id} falló: {log}");
        return Assert.Single(Directory.GetFiles(destination, $"{id}-*{PackageManifest.FileExtension}"));
    }
}

/// <summary>
/// Necesita la GPU y FFmpeg (como <see cref="HardwareFactAttribute"/>) y los repositorios de contenido clonados en una
/// carpeta <c>Complementos</c> por encima del repositorio (o en <c>RODAJE_COMPLEMENTOS</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ContentFactAttribute : FactAttribute
{
    public ContentFactAttribute() =>
        Skip = Rodaje.TestMedia.Skip.First(
            Rodaje.TestMedia.Skip.HardwareDecoding,
            Rodaje.TestMedia.Skip.Encoders([VideoCodec.H264]),
            ContentRoot is null ? "Faltan los repositorios de contenido (Complementos\\rodaje-filtros y rodaje-transiciones)." : null);

    public static string? ContentRoot { get; } = FindContentRoot();

    private static string? FindContentRoot()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_COMPLEMENTOS") is { Length: > 0 } custom)
        {
            return HasBoth(custom) ? custom : null;
        }

        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "Complementos");
            if (HasBoth(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasBoth(string root) =>
        File.Exists(Path.Combine(root, "rodaje-filtros", "empaquetar.ps1")) && File.Exists(Path.Combine(root, "rodaje-transiciones", "empaquetar.ps1"));
}
