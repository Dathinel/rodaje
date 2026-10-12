using Rodaje.Module.Subtitles;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("modulo-subtitulos", "0.1.0", "Subtítulos",
[
    new FunctionInfo("subtitulos", "Transcribir con tiempos por palabra", Inputs: ["audio", "bytes"], Outputs: ["bytes"]),
], Device: "vulkan, cpu");

// Un modelo pesado a la vez: una segunda llamada recibe «busy».
ModuleHost host = new(info) { MaxConcurrentCalls = 1 };
using Transcriber transcriber = new();
host.Register("subtitulos", transcriber.RunAsync);
return await host.RunAsync(args).ConfigureAwait(false);
