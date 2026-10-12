using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Versiones endurecidas de dos pruebas del lector de proyectos.
/// <list type="bullet">
/// <item><c>ProjectReaderFuzzTests.HandwrittenHostileInputs_GiveControlledOutcome</c> aceptaba como bueno tanto abrir
/// el archivo como rechazarlo: si el lector empezara a abrir «{"schemaVersion":6}» o «[]» como proyecto vacío, seguiría
/// en verde. Aquí cada entrada tiene que rechazarse, con el motivo que le toca.</item>
/// <item><c>ProjectSerializerTests.Deserialize_ExtremeTimes_ThrowsFormatExceptionNotOverflow</c> solo pedía que hubiera
/// algún problema en la lista. Aquí no puede quedar ninguna excepción envuelta, cada problema tiene que salir en el
/// mensaje y alguno tiene que nombrar el clip o el medio que se dañó.</item>
/// </list>
/// </summary>
public class HardenedReaderTests
{
    private const string NotJson = "su contenido no es JSON bien formado o tiene campos repetidos";
    private const string NotObject = "el JSON no es un objeto.";
    private const string NoVersion = "no indica una versión de formato reconocible";
    private const string WrongType = "El archivo tiene un dato con un tipo o un valor que no corresponde en «";
    private const string Inconsistent = "El proyecto tiene datos inconsistentes y no se puede abrir";

    public static TheoryData<string, string, string?> Hostile => new()
    {
        { "", NotJson, nameof(JsonException) },
        { "   ", NotJson, nameof(JsonException) },
        { "null", NotObject, null },
        { "[]", NotObject, null },
        { "42", NotObject, null },
        { "\"texto\"", NotObject, null },
        { "{", NotJson, nameof(JsonException) },
        { "{\"schemaVersion\":3,\"schemaVersion\":3}", NotJson, nameof(JsonException) },
        { "{\"schemaVersion\":6}", Inconsistent, null },
        { "{\"schemaVersion\":2147483647}", "fue creado con una versión más nueva de Rodaje (formato 2147483647)", null },
        { "{\"schemaVersion\":99999999999999999999}", NoVersion, null },
        { "{\"schemaVersion\":-1}", NoVersion, null },
        { "{\"id\":\"p\"}", NoVersion, null },
        { "{\"schemaVersion\":3,\"sequences\":null}", WrongType + "$.sequences»", nameof(JsonException) },
        { "{\"schemaVersion\":3,\"sequences\":[null]}", Inconsistent, null },
        { "{\"schemaVersion\":3,\"sequences\":[[]]}", WrongType + "$.sequences[0]»", nameof(JsonException) },
        { "{\"schemaVersion\":3,\"sequences\":\"x\"}", WrongType + "$.sequences»", nameof(JsonException) },
        { "{\"schemaVersion\":1,\"sequences\":{\"markers\":1}}", WrongType + "$.sequences»", nameof(JsonException) },
        { "{\"schemaVersion\":2,\"sequences\":[{\"videoTracks\":[{\"effects\":[1,null,\"x\",{\"type\":5}]}]}]}", WrongType + "$.sequences[0].videoTracks[0].effects[0]»", nameof(JsonException) },
        {
            "{\"schemaVersion\":2,\"sequences\":[{\"videoTracks\":[{\"clips\":[{\"effects\":[{\"type\":\"rodaje.volume\",\"parameters\":{\"gain\":{\"value\":\"x\",\"keyframes\":[1,{\"value\":null}]}}}]}]}]}]}",
            WrongType + "$.sequences[0].videoTracks[0].clips[0].effects[0].parameters.gainDb.value»",
            nameof(JsonException)
        },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Every_handwritten_hostile_input_is_rejected_with_its_reason(string json, string expected, string? innerBase)
    {
        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        if (innerBase is null)
        {
            // Rechazo propio del lector o del validador: nada envuelto.
            Assert.Null(ex.InnerException);
        }
        else
        {
            // Rechazo del analizador de JSON (JsonException o una derivada, como la del lector de bajo nivel).
            Assert.NotNull(ex.InnerException);
            Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        }

        // Un rechazo del validador trae la lista de problemas y cada uno aparece en el mensaje.
        Assert.Equal(expected == Inconsistent, ex.Problems.Count > 0);
        Assert.All(ex.Problems, p => Assert.Contains(p, ex.Message, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ProjectSerializerTests.ExtremeTimes), MemberType = typeof(ProjectSerializerTests))]
    public void Extreme_times_are_rejected_naming_what_is_damaged(string description, Action<JsonObject> corrupt)
    {
        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(SerializationSamples.Rich()))!.AsObject();
        string clipId = (string)root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["id"]!;
        string mediaId = (string)root["media"]![0]!["id"]!;
        corrupt(root);

        Exception? thrown = Record.Exception(() => ProjectSerializer.Deserialize(root.ToJsonString()));

        ProjectFormatException ex = Assert.IsType<ProjectFormatException>(thrown);
        Assert.True(ex.InnerException is null, $"{description}: se envolvió {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        Assert.StartsWith(Inconsistent, ex.Message, StringComparison.Ordinal);
        Assert.NotEmpty(ex.Problems);
        Assert.All(ex.Problems, p => Assert.Contains(p, ex.Message, StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains($"«{clipId}»", StringComparison.Ordinal) || p.Contains($"«{mediaId}»", StringComparison.Ordinal));
        Assert.Equal(ex.Problems.Count, ex.Problems.Distinct(StringComparer.Ordinal).Count());
    }
}
