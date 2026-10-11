using System.Reflection;
using Xunit.Sdk;

namespace Rodaje.Media.Tests.Support;

/// <summary>
/// Las filas de clips que una teoría solo puede comprobar si se generaron con su codificador exacto
/// (<see cref="TestClips.ExactEncoder"/>: GOP pedido y cuadros B). Se omiten con su motivo cuando ese codificador no
/// funciona (por ejemplo, el runner de la CI no tiene NVENC y cae en libopenh264 o libkvazaar) en lugar de fallar.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ExactEncoderClipDataAttribute : DataAttribute
{
    private readonly bool _software;
    private readonly string[] _clipFields;

    /// <param name="software">Filas (clip, modo) de <see cref="DecodeModes.Software"/>; si no, solo el nombre del clip.</param>
    /// <param name="clipFields">Nombres de los campos de <see cref="TestClips"/> (<c>"Hevc"</c>, <c>"H264Mkv"</c>...).</param>
    public ExactEncoderClipDataAttribute(bool software, params string[] clipFields)
    {
        _software = software;
        _clipFields = clipFields;
        Skip = Rodaje.TestMedia.Skip.First(Clips().Select(c => Rodaje.TestMedia.Skip.Encoder(TestClips.ExactEncoder(c.Codec))).ToArray());
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        _software ? DecodeModes.Software([.. Clips()]) : new TheoryData<string>([.. Clips().Select(c => c.Name)]);

    private IEnumerable<ClipSpec> Clips() =>
        _clipFields.Select(f => (ClipSpec)typeof(TestClips).GetField(f, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);
}
