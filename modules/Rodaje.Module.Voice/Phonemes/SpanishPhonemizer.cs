using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Phonemes;

/// <summary>
/// Español por reglas: la ortografía es casi fonémica. Letras → segmentos, diptongos e hiatos, acento por la tilde o por
/// la terminación, acentos secundarios alternos, alófonos de b/d/g según el sonido anterior (también entre palabras),
/// asimilación de la nasal y palabras átonas. <paramref name="latam"/> usa seseo y yeísmo.
/// </summary>
internal sealed class SpanishPhonemizer(bool latam) : IPhonemizer
{
    // Átonas: sin acento.
    private static readonly HashSet<string> Unstressed = new(StringComparer.Ordinal)
    {
        "el", "la", "lo", "los", "las", "le", "les", "de", "del", "a", "al", "en", "y", "e", "o", "u", "que", "se", "me", "te", "nos", "os",
        "mi", "mis", "tu", "tus", "su", "sus", "por", "con", "sin", "ni", "si", "mas", "quien", "quienes",
    };

    // Con acento secundario en la primera sílaba.
    private static readonly HashSet<string> Weak = new(StringComparer.Ordinal)
    {
        "para", "pero", "como", "cuando", "donde", "aunque", "mientras", "hasta", "desde", "sobre", "entre", "hacia", "contra",
        "mediante",
    };

    private static readonly string[] JotaX = ["méxic", "mexic", "oaxac", "xalap", "ximen", "xavier"];

    public string Phonemize(IReadOnlyList<Token> tokens)
    {
        List<List<string>?> words = [];
        foreach (Token t in tokens) words.Add(t.Kind == TokenKind.Word ? Word(t.Text) : null);
        Resolve(words);
        return Phones.Join(tokens.Select((t, i) => (t.Kind == TokenKind.Punct, t.Kind == TokenKind.Punct ? t.Text : string.Concat(words[i]!))));
    }

    private sealed class Seg(string ph, bool vowel = false, char vowelBase = '\0', bool accented = false, bool fromY = false)
    {
        // i/u que no se vuelve semivocal: tras líquida inicial o en grupo («ruedas», «criatura»).
        public bool Hiatus { get; set; }

        public string Ph { get; set; } = ph;
        public bool Vowel { get; } = vowel;
        public char Base { get; } = vowelBase;
        public bool Accented { get; } = accented;
        public bool FromY { get; } = fromY;
        public bool Strong => Base is 'a' or 'e' or 'o' || Accented;
    }

    internal List<string> Word(string word)
    {
        string w = word.ToLowerInvariant();
        if (w == "ser") return ["s", Phones.Primary, "e", "r"];
        // Adverbios en -mente: dos acentos, el del adjetivo y el de «mente».
        if (w.Length > 7 && w.EndsWith("mente", StringComparison.Ordinal) && w[^6] is 'a' or 'e' or 'l' or 'z' or 'é' or 'á' or 'n' or 'r' or 'o' or 'í')
        {
            List<string> stem = Word(w[..^5]);
            stem.RemoveAll(p => p == Phones.Secondary);
            if (!stem.Contains(Phones.Primary)) stem.Insert(0, Phones.Primary);
            stem.AddRange(["m", Phones.Primary, "e", "N", "t", "e"]);
            return stem;
        }
        List<Seg> segs = Letters(w);
        List<string> output = [];
        if (!segs.Any(s => s.Vowel))
        {
            output.AddRange(segs.Select(s => s.Ph));
            return output;
        }

        // Núcleos: índice del segmento vocálico que lleva la sílaba, con sus semivocales delante y detrás.
        List<(int Start, int Nucleus, int End)> syllables = [];
        int i = 0;
        while (i < segs.Count)
        {
            if (!segs[i].Vowel) { i++; continue; }
            int j = i;
            while (j < segs.Count && segs[j].Vowel) j++;
            Group(segs, i, j, syllables);
            i = j;
        }

        int n = syllables.Count;
        int stress = syllables.FindIndex(s => segs[s.Nucleus].Accented);
        if (stress < 0)
        {
            char last = w[^1];
            bool penult = last is 'a' or 'e' or 'i' or 'o' or 'u' or 'n' or 's' || (last == 'y' && w.Length > 1 && !IsVowelLetter(w[^2]));
            stress = n >= 2 && penult ? n - 2 : n - 1;
        }
        HashSet<int> secondary = [];
        bool unstressed = Unstressed.Contains(w);
        if (Weak.Contains(w))
        {
            secondary.Add(0);
            stress = -1;
        }
        else if (unstressed)
        {
            stress = -1;
        }
        else if (stress >= 2)
        {
            secondary.Add(0);
            for (int k = stress - 2; k >= 2; k -= 2) secondary.Add(k);
        }

        // e abierta en sílaba trabada por nasal más consonante («tengo», «gente»).
        if (stress >= 0)
        {
            (int _, int nuc, int end) = syllables[stress];
            if (segs[nuc].Base == 'e' && end == nuc + 1 && end + 1 < segs.Count && segs[end].Ph == "N" && !segs[end + 1].Vowel)
                segs[nuc].Ph = "ɛ";
            if (w.StartsWith("méxic", StringComparison.Ordinal) && segs[nuc].Base == 'e') segs[nuc].Ph = "ɛ";
        }

        int syl = 0;
        for (int k = 0; k < segs.Count; k++)
        {
            if (syl < n && k == syllables[syl].Nucleus)
            {
                if (syl == stress) output.Add(Phones.Primary);
                else if (secondary.Contains(syl)) output.Add(Phones.Secondary);
                (int _, int nuc, int end) = syllables[syl];
                output.Add(Nucleus(segs, nuc, end));
                k = end - 1;
                syl++;
                continue;
            }
            output.Add(segs[k].Ph);
        }
        // b, d y g en coda son oclusivas («ciudad» → θjuðˈad, «web», «blogs», «subcultura»).
        for (int k = 0; k < output.Count; k++)
        {
            if (output[k] is not ("B" or "D" or "G")) continue;
            string? next = output.Skip(k + 1).FirstOrDefault(p => !Phones.IsStress(p));
            bool coda = next is null || (!IsVowelPhone(next) && next is not ("ɾ" or "r" or "l" or "j" or "w"));
            if (!coda) continue;
            if (output[k] == "G") output[k] = "ɡ";
            else if (output[k] == "B" && (next is null or "k" or "t" or "p")) output[k] = "b";
            else if (output[k] == "D" && next is null) output[k] = "Df";
        }
        return output;
    }

    // Una racha de vocales: las fuertes son núcleo; una débil junto a una fuerte es semivocal; dos débiles, la segunda
    // es núcleo («ciudad», «cuidado») salvo la «y» final («muy»).
    private static void Group(List<Seg> segs, int from, int to, List<(int, int, int)> syllables)
    {
        int k = from;
        while (k < to)
        {
            int start = k;
            while (k < to && !segs[k].Strong && k + 1 < to && !(segs[k + 1].FromY && !segs[k + 1].Strong && k + 2 == to)
                && !segs[k].Hiatus)
            {
                segs[k].Ph = segs[k].Base == 'u' ? "w" : "j";
                k++;
            }
            int nucleus = k;
            k++;
            // Semivocal detrás: una débil seguida de nada o de otra fuerte que empieza su propia sílaba.
            if (k < to && !segs[k].Strong && (k + 1 == to || segs[k + 1].Strong))
                k++;
            syllables.Add((start, nucleus, k));
        }
    }

    private static string Nucleus(List<Seg> segs, int nucleus, int end)
    {
        string v = segs[nucleus].Ph;
        if (end == nucleus + 1) return v;
        char glide = segs[nucleus + 1].Base;
        return (v, glide) switch
        {
            ("a", 'i') => "I",
            ("a", 'u') => "W",
            ("e", 'i') => "A",
            ("o", 'i') => "oɪ",
            ("e", 'u') => "eʊ",
            ("o", 'u') => "O",
            _ => v + (glide == 'u' ? "w" : "j"),
        };
    }

    private List<Seg> Letters(string w)
    {
        List<Seg> s = [];
        string ce = latam ? "s" : "θ";
        string ll = latam ? "ʝ" : "ʎ";
        bool jotaX = JotaX.Any(p => w.StartsWith(p, StringComparison.Ordinal));
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            char next = i + 1 < w.Length ? w[i + 1] : '\0';
            char after = i + 2 < w.Length ? w[i + 2] : '\0';
            switch (c)
            {
                case 'a' or 'e' or 'i' or 'o' or 'u' or 'ü' or 'à' or 'è' or 'ì' or 'ò' or 'ù' or 'â' or 'ê' or 'î' or 'ô' or 'û' or 'ä' or 'ë' or 'ï' or 'ö':
                    char b = Base(c);
                    s.Add(new Seg(b.ToString(), vowel: true, vowelBase: b)
                    {
                        Hiatus = (b is 'i' or 'u' && IsStrongLetter(next) && AfterLiquidOnset(s)),
                    });
                    break;
                case 'á' or 'é' or 'í' or 'ó' or 'ú':
                    char a = Base(c);
                    s.Add(new Seg(a.ToString(), vowel: true, vowelBase: a, accented: true));
                    break;
                case 'b' or 'v':
                    s.Add(new Seg("B"));
                    break;
                case 'c':
                    if (next == 'h') { s.Add(new Seg("ʧ")); i++; }
                    else if (next == 'k') { s.Add(new Seg("k")); i++; }
                    else if (IsFront(next)) s.Add(new Seg(ce));
                    else s.Add(new Seg("k"));
                    break;
                case 'd':
                    s.Add(new Seg("D"));
                    break;
                case 'g':
                    if (IsFront(next)) s.Add(new Seg("x"));
                    else if (next == 'u' && IsFront(after)) { s.Add(new Seg("G")); i++; }
                    else s.Add(new Seg("G"));
                    break;
                case 'h':
                    break;
                case 'j':
                    s.Add(new Seg("x"));
                    break;
                case 'l':
                    if (next == 'l') { s.Add(new Seg(ll)); i++; }
                    else s.Add(new Seg("l"));
                    break;
                case 'n':
                    s.Add(new Seg("N"));
                    break;
                case 'ñ':
                    s.Add(new Seg("ɲ"));
                    break;
                case 'p':
                    if (!(i == 0 && next is 's' or 'n' or 't')) s.Add(new Seg("p"));
                    break;
                case 'q':
                    s.Add(new Seg("k"));
                    if (next == 'u') i++;
                    break;
                case 'r':
                    if (next == 'r') { s.Add(new Seg("r")); i++; }
                    else if (i == 0 || w[i - 1] is 'l' or 'n' or 's') s.Add(new Seg("r"));
                    else s.Add(new Seg("ɾ"));
                    break;
                case 'w':
                    s.Add(new Seg("w"));
                    break;
                case 'x':
                    if (jotaX) s.Add(new Seg("x"));
                    else if (i == 0) s.Add(new Seg("s"));
                    else { s.Add(new Seg("k")); s.Add(new Seg("s")); }
                    break;
                case 'y':
                    bool vowelY = w.Length == 1 || (i > 0 && next == '\0') || (i > 0 && IsVowelLetter(w[i - 1]) && !IsVowelLetter(next));
                    if (vowelY) s.Add(new Seg("i", vowel: true, vowelBase: 'i', fromY: true));
                    else s.Add(new Seg("ʝ"));
                    break;
                case 'z':
                    s.Add(new Seg(ce));
                    break;
                case 'ç':
                    s.Add(new Seg("s"));
                    break;
                case 's' when next == 'h':
                    s.Add(new Seg("ʃ"));
                    i++;
                    break;
                case 'k' or 'f' or 'm' or 's' or 't':
                    s.Add(new Seg(c.ToString()));
                    break;
                default:
                    break;
            }
        }
        return s;
    }

    // Tras líquida al principio de la palabra («ruedas»), en grupo con consonante («criatura», «influencia») o tras ʎ.
    private static bool AfterLiquidOnset(List<Seg> s) =>
        s.Count > 0 && (s[^1].Ph == "ʎ" || (s[^1].Ph is "ɾ" or "r" or "l" && (s.Count == 1 || !s[^2].Vowel)));

    private static bool IsStrongLetter(char c) => c is 'a' or 'e' or 'o' or 'á' or 'é' or 'ó' or 'í' or 'ú';

    private static bool IsFront(char c) => c is 'e' or 'i' or 'é' or 'í' or 'y';

    private static bool IsVowelLetter(char c) => "aeiouáéíóúü".Contains(c, StringComparison.Ordinal);

    private static char Base(char c) => c switch
    {
        'á' or 'à' or 'â' or 'ä' => 'a',
        'é' or 'è' or 'ê' or 'ë' => 'e',
        'í' or 'ì' or 'î' or 'ï' => 'i',
        'ó' or 'ò' or 'ô' or 'ö' => 'o',
        'ú' or 'ù' or 'û' or 'ü' => 'u',
        _ => c,
    };

    private static bool IsVowelPhone(string p) => p.Length > 0 && "aeiouɛIWAOɔ".Contains(p[0], StringComparison.Ordinal);

    private static bool IsNasal(string? p) => p is "m" or "n" or "ɲ" or "ŋ" or "N";

    // Alófonos y asimilación sobre toda la frase: la puntuación es pausa (vuelve la oclusiva).
    private static void Resolve(List<List<string>?> words)
    {
        string? prev = null;
        for (int wi = 0; wi < words.Count; wi++)
        {
            List<string>? w = words[wi];
            if (w is null) { prev = null; continue; }
            for (int k = 0; k < w.Count; k++)
            {
                string p = w[k];
                if (Phones.IsStress(p)) continue;
                switch (p)
                {
                    case "B":
                        w[k] = prev is null || IsNasal(prev) ? "b" : "β";
                        break;
                    case "D":
                        w[k] = prev is null || IsNasal(prev) ? "d" : "ð";
                        break;
                    case "Df":
                        // d final: oclusiva ante pausa («ciudad.»), fricativa si sigue otra palabra.
                        w[k] = wi + 1 < words.Count && words[wi + 1] is not null ? "ð" : "d";
                        break;
                    case "G":
                        w[k] = prev is null || IsNasal(prev) ? "ɡ" : "ɣ";
                        break;
                    case "N":
                        (string? next, int nk) = Next(words, wi, k);
                        bool sameWord = nk >= 0;
                        if (next is "B" or "b" or "p") w[k] = "m";
                        else if (next is "G" or "ɡ" or "x" && (sameWord || k == w.Count - 1)) w[k] = "ŋ";
                        else if (next == "ʝ" && nk >= 0) { w[k] = "ɲ"; w[nk] = ""; }
                        else w[k] = "n";
                        break;
                }
                if (w[k].Length > 0) prev = w[k];
            }
        }
    }

    // El siguiente fonema (sin marcas de acento), cruzando a la palabra siguiente; nk es su índice si está en la misma.
    private static (string? Next, int Index) Next(List<List<string>?> words, int wi, int k)
    {
        List<string> w = words[wi]!;
        for (int j = k + 1; j < w.Count; j++)
            if (!Phones.IsStress(w[j]) && w[j].Length > 0) return (w[j], j);
        if (wi + 1 < words.Count && words[wi + 1] is { } nextWord)
            foreach (string p in nextWord)
                if (!Phones.IsStress(p) && p.Length > 0) return (p, -1);
        return (null, -1);
    }
}
