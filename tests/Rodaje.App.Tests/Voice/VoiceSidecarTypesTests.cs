using Rodaje.App.Services.Voice;

namespace Rodaje.App.Tests.Voice;

/// <summary>
/// El <c>.json</c> de voz con tipos cambiados a mano: <c>generator</c> que no es texto lanzaba InvalidOperationException
/// (solo se atrapaba JsonException).
/// </summary>
public sealed class VoiceSidecarTypesTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"modulo-voz\"]")]
    public void UnGeneradorQueNoEsTexto_DevuelveNull(string generator) =>
        Assert.Null(VoiceSettings.FromSidecar($"{{\"generator\":{generator},\"text\":\"Hola\"}}"));
}
