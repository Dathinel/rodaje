using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Engine.Timeline;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.Effects;

/// <summary>Transiciones y efectos de plantilla de movimiento (D-064) leídos de los propios e instanciados con valores.</summary>
public sealed class TemplateContentTests : IDisposable
{
    private const string Barrido = """
        { "format": "rodaje.template", "version": 1, "name": "Barrido", "kind": "transition", "durationSeconds": 0.8, "minSeconds": 0.2,
          "body": { "format": "rodaje.preset", "version": 2, "name": "Barrido", "kind": "transition", "durationSeconds": 0.8,
                    "mix": "rodaje.wipe", "mixParameters": { "angle": 0, "softness": 0.1 } },
          "parameters": [ { "id": "angulo", "label": "Ángulo", "type": "number", "min": -180, "max": 180, "targets": ["mixParameters/angle"] } ] }
        """;

    private const string Zoom = """
        { "format": "rodaje.template", "version": 1, "name": "Zoom", "kind": "effect", "durationSeconds": 0.6, "minSeconds": 0.6,
          "body": { "format": "rodaje.effect", "version": 1, "name": "Zoom", "anchor": "start", "durationSeconds": 0.6, "effects": [],
                    "curves": [ { "effect": "rodaje.transform", "parameter": "scale", "keys": [ { "t": 0, "value": 1.3 }, { "t": 0.6, "value": 1 } ] } ] },
          "parameters": [ { "id": "zoom", "label": "Zoom", "type": "number", "min": 1, "max": 3, "targets": ["curves/0/keys/0/value"] } ] }
        """;

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private InstalledContentLoader Loader()
    {
        string own = Path.Combine(_folder.Path, "propios");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "barrido.json"), Barrido);
        File.WriteAllText(Path.Combine(own, "zoom.json"), Zoom);
        PackageScan scan = PackageLibrary.Scan(new PackageLocations(Path.Combine(_folder.Path, "p"), Path.Combine(_folder.Path, "m"), own, []), "rodaje");
        return new InstalledContentLoader(() => scan);
    }

    [Fact]
    public void Un_propio_con_formato_de_plantilla_es_motion_template_aunque_su_kind_sea_transition()
    {
        string own = Path.Combine(_folder.Path, "propios");
        _ = Loader();
        PackageScan scan = PackageLibrary.Scan(new PackageLocations(Path.Combine(_folder.Path, "p"), Path.Combine(_folder.Path, "m"), own, []), "rodaje");

        Assert.All(scan.Find(PackageLibrary.OwnPackageId)!.Manifest.Items, i => Assert.Equal(ItemKind.MotionTemplate, i.Kind));
    }

    [Fact]
    public void La_transicion_cambia_de_angulo_con_sus_valores_y_cada_combinacion_se_lee_una_vez()
    {
        var content = new EffectContent(Loader());

        TransitionCurves plain = content.FindTransition("propios@0.0.0/barrido", null)!;
        TransitionCurves turned = content.FindTransition("propios@0.0.0/barrido", "{\"angulo\":90}")!;

        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code, turned.MixCode);
        Assert.Equal(0, plain.MixParameters["angle"]);
        Assert.Equal(90, turned.MixParameters["angle"]);
        Assert.Same(turned, content.FindTransition("propios@0.0.0/barrido", "{\"angulo\":90}"));
        Assert.Same(plain, content.FindTransition("propios@0.0.0/barrido"));
    }

    [Fact]
    public void Un_valor_fuera_de_rango_no_da_transicion_y_avisa()
    {
        var content = new EffectContent(Loader());
        var problems = new List<string>();
        content.Problem += (_, e) => problems.Add(e.Message);

        Assert.Null(content.FindTransition("propios@0.0.0/barrido", "{\"angulo\":500}"));
        Assert.Single(problems);
    }

    [Fact]
    public void Reaplicar_un_efecto_con_otros_valores_reemplaza_lo_aplicado_en_un_paso_y_deshacer_vuelve_a_los_bytes() => UiThread.Run(async () =>
    {
        EditorSession session = await EffectCommandsTests.TwoClips();
        InstalledContentLoader loader = Loader();
        string clipId = session.MainTrack!.Clips[0].Id;
        const string reference = "propios@0.0.0/zoom";

        Assert.True(session.Execute(EffectCommands.ApplyEffect(session.SequenceId, clipId, loader.LoadEffect(reference, null)!, reference, "{}")));
        string before = ProjectSerializer.Serialize(session.Project);
        int steps = session.History.Count;

        Assert.True(session.Execute(EffectCommands.ApplyEffect(session.SequenceId, clipId, loader.LoadEffect(reference, "{\"zoom\":2}")!, reference, "{\"zoom\":2}")));

        EffectInstance transform = session.MainTrack!.Clips[0].Effects.Single(e => e.Options.GetValueOrDefault(BuiltInEffects.CommonOptions.Source) == reference);
        Assert.Equal("{\"zoom\":2}", transform.Options[BuiltInEffects.CommonOptions.TemplateValues]);
        Assert.Equal(2, transform.Parameters["scale"].Keyframes[0].Value);
        Assert.Equal(steps + 1, session.History.Count);

        session.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(session.Project));
    });
}
