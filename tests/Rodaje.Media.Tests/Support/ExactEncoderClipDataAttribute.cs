using System.Reflection;
using Xunit.Sdk;

namespace Rodaje.Media.Tests.Support;

/// <summary>
/// Las filas de clips que una teoría solo puede comprobar si se generaron con su codificador exacto
/// (<see cref="TestClips.ExactEncoder"/>). Se omiten, con el motivo que da la prueba, cuando ese codificador no funciona
/// (el runner de la CI no tiene NVENC y el clip sale de libopenh264 o libkvazaar) en lugar de fallar.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class ExactEncoderClipDataAttribute : DataAttribute
{
    private readonly bool _software;
    private readonly string[] _clipFields;

    /// <param name="software">Filas (clip, modo) de <see cref="DecodeModes.Software"/>; si no, solo el nombre del clip.</param>
    /// <param name="because">Por qué el clip sin su codificador exacto no sirve para esta prueba.</param>
    /// <param name="clipFields">Nombres de los campos de <see cref="TestClips"/> (<c>"Hevc"</c>, <c>"H264Mkv"</c>...).</param>
    public ExactEncoderClipDataAttribute(bool software, string because, params string[] clipFields)
    {
        _software = software;
        _clipFields = clipFields;
        string? missing = Clips().Select(c => TestClips.ExactEncoder(c.Codec))
            .FirstOrDefault(e => Rodaje.TestMedia.Skip.Encoder(e) is not null);
        Skip = missing is null ? null : $"Omitida: sin {missing} en este equipo, {because}";
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        _software ? DecodeModes.Software([.. Clips()]) : new TheoryData<string>([.. Clips().Select(c => c.Name)]);

    private IEnumerable<ClipSpec> Clips() =>
        _clipFields.Select(f => (ClipSpec)typeof(TestClips).GetField(f, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);
}
