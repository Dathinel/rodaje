using Rodaje.Module.Translate;
using Rodaje.Modules.Protocol;

ModuleInfo info = new("modulo-traducir", "1.0.1", "Traducir",
[
    new FunctionInfo("traducir", "Traducir subtítulos conservando ids y tiempos", Inputs: ["bytes", "bytes"], Outputs: ["bytes"]),
], Device: "vulkan, cpu");

// Un modelo pesado a la vez: una segunda llamada recibe «busy».
ModuleHost host = new(info) { MaxConcurrentCalls = 1 };
using TranslateFunction translate = new();
host.Register("traducir", translate.RunAsync);
return await host.RunAsync(args).ConfigureAwait(false);
