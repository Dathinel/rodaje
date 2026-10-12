using System.IO.Compression;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Packages;

public sealed class MissingContentTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Lista_las_LUT_y_transiciones_de_paquetes_que_no_estan_sin_repetir()
    {
        var clip = new Clip { Id = "a" };
        clip.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/lut-calido"));
        clip.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "file:luts/mia.cube"));
        clip.Effects.Add(Effect("otro.efecto", "lut", "nadie@1.0.0/x"));
        var track = new Track { Id = "v", Kind = TrackKind.Video, Clips = [clip] };
        track.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/lut-calido"));
        track.Transitions.Add(new TransitionInstance
        {
            Id = "t",
            Type = BuiltInTransitions.Preset,
            Options = new() { [BuiltInTransitions.PresetOptions.Preset] = "rodaje-transiciones@1.0.0/girar" },
        });
        var sequence = new Sequence { Id = "s", VideoTracks = [track] };
        var project = new Project { Id = "p", Sequences = [sequence] };

        IReadOnlyList<PackageReference> missing = MissingContent.Find(project, PackageScan.Empty);

        Assert.Equal(["rodaje-filtros@1.0.0/lut-calido", "rodaje-transiciones@1.0.0/girar"], missing.Select(r => r.ToString()));
        string text = MissingContent.Describe(missing);
        Assert.Contains("«rodaje-filtros», «rodaje-transiciones»", text, StringComparison.Ordinal);
        Assert.Contains("fundido cruzado", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Desinstalar_con_el_proyecto_abierto_avisa_una_vez_de_lo_que_empieza_a_faltar() => UiThread.Run(async () =>
    {
        var packages = new PackageService(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog());
        string filters = WriteArchive("rodaje-filtros");
        string other = WriteArchive("otro-paquete");
        await packages.InstallFromFileAsync(filters);
        await packages.InstallFromFileAsync(other);

        var track = new Track { Id = "v", Kind = TrackKind.Video };
        track.Effects.Add(Effect(BuiltInEffects.Color, BuiltInEffects.ColorOptions.Lut, "rodaje-filtros@1.0.0/elemento"));
        string projectPath = Path.Combine(_folder.Path, "p.rodaje");
        ProjectSerializer.Save(new Project { Id = "p", Name = "p", Sequences = [new Sequence { Id = "s", Name = "s", VideoTracks = [track] }] }, projectPath);

        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        var warnings = new List<EditorNotification>();
        session.Notified += (_, n) =>
        {
            if (n.Severity is NotificationSeverity.Warning or NotificationSeverity.Error)
            {
                warnings.Add(n);
            }
        };
        MissingContent.Attach(session, packages);
        Assert.True(await session.OpenProjectAsync(projectPath), string.Join(" | ", warnings.Select(w => w.Message)));
        await packages.RefreshAsync();
        Assert.Empty(warnings);

        await packages.UninstallAsync("otro-paquete");
        Assert.Empty(warnings);

        await packages.UninstallAsync("rodaje-filtros");
        EditorNotification notice = Assert.Single(warnings);
        Assert.Contains("«rodaje-filtros»", notice.Message, StringComparison.Ordinal);

        await packages.RefreshAsync();
        Assert.Single(warnings);

        await packages.InstallFromFileAsync(filters);
        await packages.UninstallAsync("rodaje-filtros");
        Assert.Equal(2, warnings.Count);
    });

    private string WriteArchive(string id)
    {
        string path = Path.Combine(_folder.Path, id + ".paquete");
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("paquete.json").Open());
        writer.Write($$"""
            {
              "formatVersion": 1, "id": "{{id}}", "version": "1.0.0", "name": "{{id}}", "author": "Dathinel", "license": "MIT",
              "apps": ["rodaje"], "items": [{ "id": "elemento", "kind": "transition", "name": "Elemento" }]
            }
            """);
        return path;
    }

    private static EffectInstance Effect(string type, string option, string value) =>
        new() { Id = Guid.NewGuid().ToString("N"), Type = type, Options = new() { [option] = value } };
}
