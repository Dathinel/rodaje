using System.Diagnostics;
using System.Text;
using Rodaje.Module.Voice;
using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;
using Rodaje.Modules.Protocol;

string? root = Voices.FindRoot();

// Herramientas de línea de comandos para probar sin el editor:
//   --fonemas <idioma>                          lee líneas de la entrada y escribe «línea<TAB>fonemas»
//   --decir <idioma> <voz> <salida.wav> [texto]  sintetiza (o lee el texto de la entrada) y escribe un WAV
if (args.Length >= 2 && args[0] is "--fonemas" or "--decir")
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.InputEncoding = Encoding.UTF8;
    if (!Languages.TryParse(args[1], out Language language))
    {
        Console.Error.WriteLine("Idioma desconocido: " + args[1]);
        return 64;
    }
    if (root is null && (args[0] == "--decir" || language.IsEnglish()))
    {
        Console.Error.WriteLine("No encuentro modelos\\voz (prepáralo con build\\preparar-modelos.ps1).");
        return 66;
    }
    using Synthesizer synth = new(root ?? "");
    if (args[0] == "--fonemas")
    {
        var phonemizer = synth.Phonemizer(language);
        while (Console.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            var parts = Synthesizer.Plan(new SpeechRequest(line, language, "ef_dora", SentencePause: 0), phonemizer);
            Console.WriteLine(line + "\t" + string.Join(" ", parts.Select(p => p.Phonemes).Where(p => p.Length > 0)));
        }
        return 0;
    }
    if (args.Length < 4) return 64;
    string text = args.Length > 4 ? string.Join(' ', args[4..]) : Console.In.ReadToEnd();
    // Carga el modelo y calienta la sesión antes de medir.
    Stopwatch load = Stopwatch.StartNew();
    synth.Speak(new SpeechRequest("Hola.", language, args[2]), null, CancellationToken.None);
    Console.WriteLine($"Carga y primera frase: {load.Elapsed.TotalSeconds:F2} s");
    Stopwatch sw = Stopwatch.StartNew();
    SpeechResult r = synth.Speak(new SpeechRequest(text, language, args[2]), null, CancellationToken.None);
    double seconds = sw.Elapsed.TotalSeconds;
    Wav.WriteMonoFloat(args[3], r.Audio, r.SampleRate);
    double duration = (double)r.Audio.Length / r.SampleRate;
    Console.WriteLine($"{duration:F2} s de audio en {seconds:F2} s ({duration / seconds:F1}× tiempo real)");
    Console.WriteLine(r.Phonemes);
    return 0;
}

using VoiceFunctions functions = new(root);
// Kokoro usa todos los núcleos que se le dan: una síntesis a la vez.
ModuleHost host = new(VoiceFunctions.Info) { MaxConcurrentCalls = 2 };
host.Register("voces", functions.VoicesAsync);
host.Register("voz", functions.SpeakAsync);
host.Register("entregar", functions.DeliverAsync);
return await host.RunAsync(args).ConfigureAwait(false);
