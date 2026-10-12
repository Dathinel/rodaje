using Rodaje.Module.Voice.Phonemes;
using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Synthesis;

internal sealed record SpeechRequest(string Text, Language Language, string Voice, double Speed = 1, double Pitch = 0,
    double SentencePause = 0.4, double CommaPause = 0);

/// <summary>Un tramo del audio en segundos: una frase o, aproximada por el largo de sus fonemas, una palabra.</summary>
internal sealed record TimedText(double Start, double End, string Text);

internal sealed record SpeechResult(float[] Audio, int SampleRate, IReadOnlyList<TimedText> Segments, IReadOnlyList<TimedText> Words, string Phonemes);

/// <summary>
/// Texto → audio: frases, fonemas, trozos de hasta 510 símbolos para Kokoro, silencio recortado en los bordes de cada
/// trozo, pausas exactas entre frases y tono por remuestreo (Kokoro habla más lento o más rápido para compensar la
/// duración).
/// </summary>
internal sealed class Synthesizer(string root) : IDisposable
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2;
    public const double MaxPitch = 6;
    public const int MaxTextLength = 20_000;

    private readonly Lock _gate = new();
    private KokoroEngine? _engine;
    private readonly Dictionary<Language, EnglishLexicon> _lexicons = [];

    public string Root { get; } = root;

    public int Threads { get; init; }

    public static List<(string Phonemes, string Text, IReadOnlyList<Token> Tokens, double PauseAfter)> Plan(SpeechRequest r, IPhonemizer phonemizer)
    {
        List<(string, string, IReadOnlyList<Token>, double)> chunks = [];
        foreach (Sentence s in Script.Parse(r.Text, r.Language, r.SentencePause, r.CommaPause))
        {
            if (s.Tokens.Count == 0)
            {
                chunks.Add(("", "", [], s.PauseAfter));
                continue;
            }
            foreach (List<Token> part in SplitLong(s.Tokens, phonemizer))
                chunks.Add((phonemizer.Phonemize(part), string.Join(' ', part.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text)), part, 0));
            var last = chunks[^1];
            chunks[^1] = (last.Item1, last.Item2, last.Item3, s.PauseAfter);
        }
        return chunks;
    }

    // Una frase demasiado larga se parte en la última coma o, si no hay, entre palabras.
    private static IEnumerable<List<Token>> SplitLong(IReadOnlyList<Token> tokens, IPhonemizer phonemizer)
    {
        List<Token> current = [];
        int lastComma = -1;
        foreach (Token t in tokens)
        {
            current.Add(t);
            if (t.Kind == TokenKind.Punct && t.Text is "," or ";" or ":" or "—") lastComma = current.Count;
            if (KokoroEngine.CountTokens(phonemizer.Phonemize(current)) <= KokoroEngine.MaxTokens - 10) continue;
            int cut = lastComma > 0 ? lastComma : current.Count - 1;
            if (cut <= 0) cut = current.Count;
            yield return current[..cut];
            current = current[cut..];
            lastComma = -1;
        }
        if (current.Any(t => t.Kind == TokenKind.Word)) yield return current;
    }

    public IPhonemizer Phonemizer(Language language)
    {
        if (!language.IsEnglish()) return Phonemizers.For(language, null);
        lock (_gate)
        {
            if (!_lexicons.TryGetValue(language, out EnglishLexicon? lex))
            {
                string prefix = language == Language.EnglishUk ? "gb" : "us";
                lex = EnglishLexicon.Load(Voices.LexiconFile(Root, prefix + "_gold"), Voices.LexiconFile(Root, prefix + "_silver"));
                _lexicons[language] = lex;
            }
            return Phonemizers.For(language, lex);
        }
    }

    public KokoroEngine Engine
    {
        get
        {
            lock (_gate) return _engine ??= new KokoroEngine(Root, Threads);
        }
    }

    /// <summary>Trozos que se sintetizan a la vez: los LSTM de Kokoro son secuenciales y un solo trozo no llena la CPU.</summary>
    public static int DefaultParallelism() =>
        int.TryParse(Environment.GetEnvironmentVariable("RODAJE_VOZ_PARALELO"), out int n) && n > 0 ? n : Math.Clamp(Environment.ProcessorCount / 5, 1, 3);

    public int Parallelism { get; init; } = DefaultParallelism();

    public SpeechResult Speak(SpeechRequest r, Action<double, string>? progress, CancellationToken cancellationToken)
    {
        progress?.Invoke(0, "fonemas");
        var plan = Plan(r, Phonemizer(r.Language));
        double pitch = Math.Pow(2, Math.Clamp(r.Pitch, -MaxPitch, MaxPitch) / 12);
        float modelSpeed = (float)(Math.Clamp(r.Speed, MinSpeed, MaxSpeed) / pitch);
        int sr = KokoroEngine.SampleRate;
        int total = Math.Max(1, plan.Sum(c => KokoroEngine.CountTokens(c.Phonemes)));
        int done = 0;
        KokoroEngine engine = Engine;
        progress?.Invoke(0, "voz");

        float[][] chunks = new float[plan.Count][];
        Lock progressGate = new();
        ParallelOptions options = new() { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken };
        try
        {
            Parallel.For(0, plan.Count, options, i =>
            {
                string phonemes = plan[i].Phonemes;
                if (phonemes.Length == 0)
                {
                    chunks[i] = [];
                    return;
                }
                float[] raw = engine.Run(phonemes, r.Voice, modelSpeed, cancellationToken);
                ReadOnlySpan<float> trimmed = AudioOps.TrimSilence(raw, sr);
                chunks[i] = pitch == 1 ? trimmed.ToArray() : AudioOps.Resample(trimmed, pitch);
                lock (progressGate)
                {
                    done += KokoroEngine.CountTokens(phonemes);
                    progress?.Invoke((double)done / total, "voz");
                }
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
            throw;
        }

        List<float> audio = [];
        List<TimedText> segments = [], words = [];
        for (int i = 0; i < plan.Count; i++)
        {
            var (phonemes, text, tokens, pauseAfter) = plan[i];
            if (chunks[i].Length > 0)
            {
                double start = (double)audio.Count / sr;
                audio.AddRange(chunks[i]);
                double end = (double)audio.Count / sr;
                segments.Add(new TimedText(start, end, text));
                words.AddRange(SpreadWords(tokens, phonemes, start, end));
            }
            // La pausa tras la última frase sobra en el clip, salvo una marca explícita.
            bool last = i == plan.Count - 1;
            if (last && phonemes.Length > 0 && Math.Abs(pauseAfter - r.SentencePause) < 1e-9) continue;
            int pause = (int)Math.Round(Math.Clamp(pauseAfter, 0, Script.MaxPause) * sr);
            audio.AddRange(new float[pause]);
        }
        progress?.Invoke(1, "voz");
        return new SpeechResult([.. audio], sr, segments, words, string.Join(" | ", plan.Select(p => p.Phonemes).Where(p => p.Length > 0)));
    }

    // Reparte el tramo de la frase entre sus palabras según cuántos fonemas tiene cada una (aproximado).
    private static IEnumerable<TimedText> SpreadWords(IReadOnlyList<Token> tokens, string phonemes, double start, double end)
    {
        List<string> wordTexts = [.. tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text)];
        string[] items = phonemes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(i => i.Any(char.IsLetter)).ToArray();
        double[] weights = items.Length == wordTexts.Count
            ? [.. items.Select(i => (double)Math.Max(1, KokoroEngine.CountTokens(i)))]
            : [.. wordTexts.Select(w => (double)Math.Max(1, w.Length))];
        double sum = weights.Sum();
        double t = start;
        for (int i = 0; i < wordTexts.Count; i++)
        {
            double d = (end - start) * weights[i] / sum;
            yield return new TimedText(t, t + d, wordTexts[i]);
            t += d;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
