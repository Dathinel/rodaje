using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Los controles del guardado y la apertura (<see cref="FiniteDoubleConverter"/> y <see cref="WellFormedStringConverter"/>)
/// van en <c>options.Converters</c> sobre el contexto generado, el mismo que usa el exe publicado con Native AOT
/// (<c>tests/Rodaje.AotSmoke</c>, paso «Controles del guardado y la apertura»). Si el código generado fijara el conversor de
/// <see cref="double"/> o de <see cref="string"/> de alguna propiedad, se saltaría el de las opciones sin avisar (tarea 241).
/// Estas pruebas son la red con JIT: los mismos casos que la prueba de humo, con las mismas opciones.
/// </summary>
public sealed class SaveCheckGeneratedContextTests
{
    private const string High = "a\uD83Cb";
    private const string Low = "a\uDFAC";
    private const string Effect = "$.sequences[0].masterEffects[0]";
    private const string Transition = "$.sequences[0].videoTracks[0].transitions[0]";

    [Fact]
    public void CheckedOptions_ResolveTheCheckingConverters()
    {
        foreach (JsonSerializerOptions options in new[] { ProjectJson.WriteInfo.Options, ProjectJson.LocateInfo.Options })
        {
            Assert.IsType<FiniteDoubleConverter>(options.GetConverter(typeof(double)));
            Assert.IsType<WellFormedStringConverter>(options.GetConverter(typeof(string)));
        }

        Assert.IsType<FiniteDoubleConverter>(ProjectJson.ReadInfo.Options.GetConverter(typeof(double)));
        // El esquema sale de ProjectInfo: con el conversor propio perdería el tipo «number».
        Assert.IsNotType<FiniteDoubleConverter>(ProjectJson.ProjectInfo.Options.GetConverter(typeof(double)));
    }

    public static TheoryData<string, string> UnsavableCases => new()
    {
        { "$.sequences[0].masterGainDb", "no es un número finito" },
        { $"{Effect}.parameters.extra.value", "no es un número finito" },
        { $"{Effect}.parameters.extra.keyframes[0].spatialOut", "no es un número finito" },
        { $"{Transition}.parameters.borde", "no es un número finito" },
        { "$.name", "sustituto UTF-16 suelto" },
        { $"{Effect}.options.nota", "sustituto UTF-16 suelto" },
        { $"{Effect}.options (el nombre de una clave)", "sustituto UTF-16 suelto" },
        { $"{Effect}.parameters (el nombre de una clave)", "sustituto UTF-16 suelto" },
        { $"{Transition}.parameters (el nombre de una clave)", "sustituto UTF-16 suelto" },
    };

    /// <summary>NaN, ±∞ y sustitutos sueltos, también en los diccionarios de parámetros (valores y claves) y en un <c>double?</c>.</summary>
    [Theory]
    [MemberData(nameof(UnsavableCases))]
    public void UnsavableValue_IsRejectedWithItsPath(string path, string problem)
    {
        Project project = Base();
        Break(project, path);

        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Serialize(project));

        Assert.Contains($"«{path}»", ex.Message, StringComparison.Ordinal);
        Assert.Contains(problem, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Un número que desborda en un parámetro, en un <c>double?</c> y en el diccionario de una transición se rechaza al abrir.</summary>
    [Theory]
    [InlineData($"{Effect}.parameters.extra.value", "\"value\": 7.25", "1e400")]
    [InlineData($"{Effect}.parameters.extra.keyframes[0].spatialOut", "\"spatialOut\": 2", "-1e400")]
    [InlineData($"{Transition}.parameters.borde", "\"borde\": 0.5", "1e400")]
    public void OverflowingNumber_IsRejectedOnOpenWithItsPath(string path, string original, string overflow)
    {
        string json = ProjectSerializer.Serialize(Base());
        string broken = json.Replace(original, original[..(original.IndexOf(':', StringComparison.Ordinal) + 2)] + overflow, StringComparison.Ordinal);
        Assert.NotEqual(json, broken);

        // La transición de control no une clips; el desborde se rechaza antes de validar.
        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(broken));

        Assert.Contains($"«{path}»", ex.Message, StringComparison.Ordinal);
        Assert.Contains("desborda", ex.Message, StringComparison.Ordinal);
    }

    private static Project Base()
    {
        Project project = ProjectFactory.CreateDefault("Controles del guardado", new SequentialIdGenerator(), Rodaje.Core.Timing.Rational.Fps30);
        Assert.Empty(project.Sequences[0].MasterEffects);
        project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-control",
            Type = "complemento.desconocido",
            Parameters = { ["extra"] = new Parameter { Value = 7.25, Keyframes = [new Keyframe(Rodaje.Core.Timing.Time.FromSeconds(1), 1) { SpatialOut = 2 }] } },
            Options = { ["nota"] = "bien" },
        });
        project.Sequences[0].VideoTracks[0].Transitions.Add(new TransitionInstance
        {
            Id = "tr-control",
            Type = "rodaje.disolver",
            From = "x",
            To = "y",
            Parameters = { ["borde"] = 0.5 },
            Options = { ["dir"] = "izq" },
        });
        return project;
    }

    private static void Break(Project project, string path)
    {
        Sequence sequence = project.Sequences[0];
        EffectInstance effect = sequence.MasterEffects[0];
        TransitionInstance transition = sequence.VideoTracks[0].Transitions[0];
        switch (path)
        {
            case "$.sequences[0].masterGainDb": sequence.MasterGainDb = double.NaN; break;
            case $"{Effect}.parameters.extra.value": effect.Parameters["extra"].Value = double.NaN; break;
            case $"{Effect}.parameters.extra.keyframes[0].spatialOut":
                effect.Parameters["extra"].Keyframes[0] = effect.Parameters["extra"].Keyframes[0] with { SpatialOut = double.PositiveInfinity };
                break;
            case $"{Transition}.parameters.borde": transition.Parameters["borde"] = double.NegativeInfinity; break;
            case "$.name": project.Name = High; break;
            case $"{Effect}.options.nota": effect.Options["nota"] = Low; break;
            case $"{Effect}.options (el nombre de una clave)": effect.Options[Low] = "x"; break;
            case $"{Effect}.parameters (el nombre de una clave)": effect.Parameters[High] = new Parameter { Value = 1 }; break;
            case $"{Transition}.parameters (el nombre de una clave)": transition.Parameters[Low] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }
}
