using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Module.AudioAi;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("modulo-audio-ia", "1.0.0", "Audio con IA",
[
    new FunctionInfo("ruido", "Reducir el ruido de la voz (DeepFilterNet3)", Inputs: ["audio"], Outputs: ["audio"]),
    new FunctionInfo("detectar-voz", "Probabilidad de voz cada 32 ms (Silero VAD)", Inputs: ["audio"], Outputs: ["bytes"]),
], Device: "cpu");

using var functions = new AudioAiFunctions(new ModelFiles());
ModuleHost host = new(info);

// ruido: [entrada, salida], f32 intercalado de 48 kHz, 1 o 2 canales, mismo largo. Siempre en CPU (D-034); el parámetro
// «dispositivo» de versiones anteriores se ignora.
host.Register("ruido", async (call, ct) =>
{
    if (call.Buffers.Count != 2 || call.Buffers[0].Audio is not { } layout || layout.SampleRate != 48_000 || layout.Channels is not (1 or 2)
        || layout.SampleFormat != SampleFormats.F32 || call.Buffers[1].Length != call.Buffers[0].Length
        || call.Buffers[0].Length != layout.Frames * layout.Channels * sizeof(float))
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "«ruido» necesita audio f32 de 48 kHz (1 o 2 canales) y un búfer de salida del mismo tamaño.");
    }

    NoiseResult result = await Guard(() => functions.ReduceNoiseAsync(
        () => Floats(call, 0), () => Floats(call, 1), layout.Channels, layout.Frames,
        p => call.ReportProgressAsync(p, "Limpiando la voz", ct), ct)).ConfigureAwait(false);
    return new ModuleOutput(Json($$"""{"dispositivo":"{{result.Device}}","canales":{{result.ChannelsProcessed}},"segundos":{{result.Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}"""),
        [call.Buffers[1]]);
});

// detectar-voz: [audio mono f32 de 16 kHz, salida con un float por ventana de 512 muestras].
host.Register("detectar-voz", async (call, ct) =>
{
    if (call.Buffers.Count != 2 || call.Buffers[0].Audio is not { } layout || layout.SampleRate != 16_000 || layout.Channels != 1
        || layout.SampleFormat != SampleFormats.F32 || call.Buffers[0].Length != layout.Frames * sizeof(float)
        || call.Buffers[1].Length < Rodaje.Module.AudioAi.Vad.SileroVad.WindowCount(layout.Frames) * sizeof(float))
    {
        throw new ModuleCallException(ErrorCodes.InvalidParameters, "«detectar-voz» necesita audio mono f32 de 16 kHz y una salida de 4 bytes por ventana de 512 muestras.");
    }

    long windows = await Guard(() => functions.DetectVoiceAsync(() => Floats(call, 0), () => Floats(call, 1), layout.Frames,
        p => call.ReportProgressAsync(p, "Buscando la voz", ct), ct)).ConfigureAwait(false);
    return new ModuleOutput(Json($$"""{"ventanas":{{windows}},"segundosPorVentana":0.032}"""), [call.Buffers[1]]);
});

return await host.RunAsync(args).ConfigureAwait(false);

static Span<float> Floats(ModuleCall call, int index) => MemoryMarshal.Cast<byte, float>(call.Map(call.Buffers[index]));

static JsonElement Json(string text)
{
    using JsonDocument doc = JsonDocument.Parse(text);
    return doc.RootElement.Clone();
}

// Un modelo que falta o está dañado se informa como error de la llamada, con un texto para la persona.
static async ValueTask<T> Guard<T>(Func<ValueTask<T>> work)
{
    try
    {
        return await work().ConfigureAwait(false);
    }
    catch (ModelMissingException ex)
    {
        throw new ModuleCallException(ErrorCodes.Internal, ex.Message);
    }
}
