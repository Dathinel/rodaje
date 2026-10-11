using System.Reflection;
using Xunit.Sdk;

namespace Rodaje.Media.Tests.Support;

/// <summary>
/// Las filas de <see cref="TestClips.Hevc10"/> de una teoría. Se omiten con su motivo cuando no funciona
/// <see cref="TestClips.TenBitEncoder"/>, el único que genera el clip, en lugar de fallar al generarlo.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class TenBitClipDataAttribute : DataAttribute
{
    private readonly bool _software;

    /// <param name="software">Filas (clip, modo) de <see cref="DecodeModes.Software"/>; si no, solo el nombre del clip.</param>
    public TenBitClipDataAttribute(bool software)
    {
        _software = software;
        Skip = Rodaje.TestMedia.Skip.Encoder(TestClips.TenBitEncoder);
    }

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        _software ? DecodeModes.Software(TestClips.Hevc10) : new TheoryData<string>(TestClips.Hevc10.Name);
}
