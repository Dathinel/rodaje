using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Phonemes;

/// <summary>
/// Portugués de Brasil por reglas: dígrafos, vocales nasales y diptongos nasales, acento por la tilde o por la
/// terminación, reducción de las átonas finales (a → æ, e → y, o → ʊ), palatalización de t/d ante [i], r fuerte, s
/// sonora, l final vocalizada y vocales abiertas por la tilde, por una lista de palabras frecuentes y por unas pocas
/// terminaciones. La apertura de e/o tónicas sin tilde no se deduce de la ortografía: un error ahí se oye pero no
/// impide entender.
/// </summary>
internal sealed class PortuguesePhonemizer : IPhonemizer
{
    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["o"] = "ʊ", ["os"] = "ʊs", ["a"] = "a", ["as"] = "as", ["e"] = "i", ["é"] = "ɛ", ["de"] = "ʤy", ["do"] = "dʊ",
        ["dos"] = "dʊs", ["da"] = "da", ["das"] = "das", ["no"] = "nʊ", ["nos"] = "nʊs", ["na"] = "na", ["nas"] = "nas",
        ["em"] = "ˈAŋ", ["que"] = "ky", ["me"] = "my", ["te"] = "ʧy", ["se"] = "sy", ["lhe"] = "ljy", ["lhes"] = "ljys",
        ["por"] = "por", ["com"] = "koŋ", ["sem"] = "sˈAŋ", ["mas"] = "mas", ["ou"] = "ow", ["um"] = "ũŋ", ["uns"] = "ũŋs",
        ["ao"] = "W", ["aos"] = "Ws", ["à"] = "a", ["às"] = "as", ["pelo"] = "pˈelʊ", ["pela"] = "pˈelæ", ["pelos"] = "pˈelʊs",
        ["pelas"] = "pˈelæs", ["meu"] = "meʊ", ["seu"] = "seʊ", ["eu"] = "eʊ", ["não"] = "nˌɐ̃ʊ̃", ["são"] = "sɐ̃ʊ̃",
        ["foi"] = "foɪ", ["ser"] = "ser", ["ter"] = "ter", ["você"] = "vosˌe", ["vocês"] = "vosˌes", ["está"] = "estˌa",
        ["estão"] = "estˌɐ̃ʊ̃", ["ele"] = "ˌely", ["eles"] = "ˌelys", ["ela"] = "ˌɛlæ", ["elas"] = "ˌɛlæs", ["sua"] = "sˌuæ",
        ["suas"] = "sˌuæs", ["seus"] = "seʊs", ["até"] = "atˌɛ", ["maior"] = "mIˈɔr", ["maiores"] = "mIˈɔɾys",
        ["maioria"] = "mˌIoɾˈiæ",
        ["muito"] = "mwˈiŋtʊ", ["muita"] = "mwˈiŋtæ", ["muitos"] = "mwˈiŋtʊs", ["muitas"] = "mwˈiŋtæs",
    };

    // Con acento secundario en lugar del primario.
    private static readonly HashSet<string> Weak = new(StringComparer.Ordinal)
    {
        "para", "uma", "umas", "desde", "como", "quando", "onde", "porque", "foram", "podem", "pode", "será", "seria", "fazer",
        "estava", "estavam", "tem", "têm", "vai", "vão",
    };

    // e y o tónicas abiertas en palabras frecuentes sin tilde.
    private static readonly HashSet<string> OpenVowel = new(StringComparer.Ordinal)
    {
        "ela", "elas", "era", "eram", "sete", "dez", "quero", "quer", "guerra", "terra", "janela", "janelas", "bela", "belo",
        "velho", "velha", "velhos", "velhas", "mel", "papel", "hotel", "sexo", "exceto", "certo", "certa", "perto", "festa",
        "febre", "pedra", "regra", "meta", "reta", "certeza", "nossa", "nosso", "nossos", "nossas", "porta", "portas", "morte",
        "forte", "fortes", "moro", "mora", "sol", "voz", "jovem", "olho", "olhos", "hora", "horas", "agora", "pode", "podem",
        "pobre", "roda", "bola", "escola", "cola", "moda", "nove", "logo", "obra", "rosa", "gosta", "gosto", "costa", "posso",
        "sorte", "norte", "nova", "novas", "novos", "jogos", "ovos", "corpos", "postos", "fora", "volta", "boca", "coisa",
        "cedo", "neve", "leve", "zero", "tela", "vela", "selo", "certos", "certas", "aberto", "aberta", "inferno",
        "moderno", "moderna", "modelo", "ideia", "ideias", "esse", "essa", "esses", "essas", "leste", "oeste", "floresta",
        "florestas", "inverno", "embora", "transporte", "transportes", "forma", "formas", "jovens", "inseto", "insetos",
        "mulheres", "devem", "deve", "pedem", "pede", "perde", "perdem", "sede", "neto", "neta", "terras", "guerras", "metro",
        "metros", "efeito", "efeitos", "aquela", "aquele", "aquelas", "aqueles", "comércio", "europa", "ótimo", "porto",
        "novas", "outra", "outro", "outras", "outros", "solo", "solos", "costas", "cobre",
        "estrela", "estrelas", "tempestade", "janela", "segredo",
    };

    private static readonly HashSet<string> XAsKs = new(StringComparer.Ordinal)
    {
        "sexo", "táxi", "taxi", "tóxico", "fixo", "fixa", "complexo", "reflexo", "anexo", "nexo", "axila", "léxico", "boxe", "xerox",
        "flexível", "conexão", "oxigênio", "tórax", "fênix", "ônix", "látex",
    };

    // «qu» y «gu» ante e/i con la u pronunciada.
    private static readonly string[] SoundedU =
        ["frequen", "cinquen", "tranquil", "consequ", "sequên", "sequen", "eloquen", "delinquen", "aguent", "linguiç", "pinguim", "bilíngue", "linguíst", "equin", "quinquen", "arguir", "unguent"];

    private static readonly HashSet<string> XAsS = new(StringComparer.Ordinal) { "próximo", "próxima", "máximo", "máxima", "auxílio", "trouxe", "sintaxe" };

    public string Phonemize(IReadOnlyList<Token> tokens)
    {
        List<(bool Punct, string Text)> items = [.. tokens.Select(t => (t.Kind == TokenKind.Punct, t.Kind == TokenKind.Punct ? t.Text : Word(t.Text)))];
        // Enlace entre palabras: s → z ante vocal o consonante sonora; o → w, e → j y r → ɾ ante vocal.
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i].Punct || items[i + 1].Punct || items[i].Text.Length == 0) continue;
            string next = items[i + 1].Text.TrimStart('ˈ', 'ˌ');
            if (next.Length == 0) continue;
            bool vowel = "aeiouɛɔɐæyʊIWAOYɪ".Contains(next[0], StringComparison.Ordinal);
            bool voiced = vowel || "bdɡvzʒmnɲlʎɾrʤ".Contains(next[0], StringComparison.Ordinal);
            string w = items[i].Text;
            char last = w[^1];
            if (last == 's' && voiced) w = w[..^1] + "z";
            else if (vowel && w == "ʊ") w = "u";
            else if (vowel && last == 'ʊ' && w.Contains('ˈ', StringComparison.Ordinal)) w = w[..^1] + "w";
            else if (vowel && last == 'y' && w != "ky") w = w[..^1] + "j";
            else if (vowel && last == 'r') w = w[..^1] + "ɾ";
            items[i] = (false, w);
        }
        return Phones.Join(items);
    }

    private enum Accent { None, Acute, Circumflex, Tilde }

    private sealed class Seg
    {
        public string Ph = "";
        public bool Vowel;
        public char Base;
        public Accent Accent;
        public bool Nasal;
        public bool Glide;
        public string NasalCoda = "";
        public bool CodaL;
    }

    internal static string Word(string word)
    {
        string w = word.ToLowerInvariant();
        if (Fixed.TryGetValue(w, out string? fixedPh)) return fixedPh;
        List<Seg> s = Letters(w);
        if (!s.Any(x => x.Vowel)) return string.Concat(s.Select(x => x.Ph));

        // Sílabas: un núcleo por vocal que no sea semivocal.
        List<int> nuclei = [];
        for (int i = 0; i < s.Count; i++)
        {
            if (!s[i].Vowel) continue;
            Seg v = s[i];
            if (v.Glide) continue;
            if (i > 0 && s[i - 1].Vowel && !s[i - 1].Glide && IsGlideCandidate(s, i) && nuclei.Count > 0 && nuclei[^1] == i - 1)
            {
                v.Glide = true;
                continue;
            }
            nuclei.Add(i);
        }

        int n = nuclei.Count;
        int stress = nuclei.FindIndex(i => s[i].Accent is Accent.Acute or Accent.Circumflex);
        if (stress < 0) stress = nuclei.FindIndex(i => s[i].Accent == Accent.Tilde);
        if (stress < 0)
        {
            string end = w.TrimEnd('s');
            bool penult = end.Length > 0 && (end[^1] is 'a' or 'e' or 'o' || w.EndsWith("am", StringComparison.Ordinal)
                || w.EndsWith("em", StringComparison.Ordinal) || w.EndsWith("ens", StringComparison.Ordinal));
            stress = n >= 2 && penult ? n - 2 : n - 1;
        }
        bool weak = Weak.Contains(w);
        HashSet<int> secondary = [];
        if (weak) secondary.Add(0);
        else if (stress >= 2 || (stress == 1 && n >= 3)) secondary.Add(0);
        if (!weak)
            for (int k = stress - 2; k >= 2; k -= 2) secondary.Add(k);

        // Calidad de las vocales.
        for (int k = 0; k < n; k++)
        {
            Seg v = s[nuclei[k]];
            bool stressed = k == stress;
            bool final = k == n - 1 && k != stress && IsWordFinalVowel(s, nuclei[k]);
            v.Ph = Quality(w, s, nuclei[k], v, stressed, k > stress, final);
        }

        // i/u átona ante otra vocal: semivocal («distância» → ʤistˈɐ̃ŋsjæ, «ciência» → sjˈAŋsjæ).
        for (int k = 0; k < n; k++)
        {
            int at = nuclei[k];
            if (k == stress || s[at].Nasal || s[at].Accent != Accent.None || at + 1 >= s.Count || !s[at + 1].Vowel) continue;
            if (s[at].Ph == "i" && at > 0 && !s[at - 1].Vowel && k > stress) s[at].Ph = "j";
            else if (s[at].Ph == "u" && at > 0 && !s[at - 1].Vowel && k > stress) s[at].Ph = "w";
            else if (s[at].Ph is "o" or "ʊ" && k > stress) s[at].Ph = "ʊ";
            else if (s[at].Ph is "e" or "y" && k > stress && at > 0 && !s[at - 1].Vowel) s[at].Ph = "j"; // «vídeo» → vˈiʤjʊ
        }

        // Palatalización de t/d ante [i].
        for (int i = 0; i + 1 < s.Count; i++)
        {
            if (s[i].Ph is "t" or "d" && s[i + 1].Vowel && s[i + 1].Ph is "i" or "y" or "j" && !s[i + 1].Nasal)
                s[i].Ph = s[i].Ph == "t" ? "ʧ" : "ʤ";
            else if (s[i].Ph is "t" or "d" && s[i + 1].Vowel && s[i + 1].Base == 'i')
                s[i].Ph = s[i].Ph == "t" ? "ʧ" : "ʤ";
        }

        System.Text.StringBuilder sb = new();
        int syl = 0;
        for (int i = 0; i < s.Count; i++)
        {
            Seg x = s[i];
            if (x.Vowel && !x.Glide)
            {
                if (syl == stress && !weak) sb.Append('ˈ');
                else if (secondary.Contains(syl)) sb.Append('ˌ');
                syl++;
                // Diptongo decreciente: la semivocal se une al núcleo.
                if (i + 1 < s.Count && s[i + 1].Vowel && s[i + 1].Glide)
                {
                    sb.Append(Diphthong(x, s[i + 1]));
                    i++;
                    x = s[i];
                }
                else
                {
                    sb.Append(x.Ph);
                }
                if (x.NasalCoda.Length > 0) sb.Append(x.NasalCoda);
                if (x.CodaL) sb.Append(x.Ph.StartsWith('u') ? "w" : "ʊ");
                continue;
            }
            sb.Append(x.Ph);
        }
        return sb.ToString().Replace("aʊ", "W", StringComparison.Ordinal).Replace("æʊ", "W", StringComparison.Ordinal)
            .Replace("eɪ", "A", StringComparison.Ordinal);
    }

    private static bool IsWordFinalVowel(List<Seg> s, int i)
    {
        for (int j = i + 1; j < s.Count; j++)
            if (!(s[j].Ph == "s" || (s[j].Vowel && s[j].Glide))) return false;
        return true;
    }

    // Una i/u átona tras vocal es semivocal salvo ante nh, nasal o r/l/z que cierra («ainda», «rainha», «juiz»).
    private static bool IsGlideCandidate(List<Seg> s, int i)
    {
        Seg v = s[i];
        if (v.Base is not ('i' or 'u') || v.Accent != Accent.None) return v.Base is 'o' or 'e' && v.Accent == Accent.None && s[i - 1].Accent == Accent.Tilde;
        if (v.Nasal) return false;
        if (i + 1 < s.Count)
        {
            Seg next = s[i + 1];
            if (next.Ph is "ɲ") return false;
            if (next.Vowel) return false;
            if (next.Ph is "ɾ" or "l" or "z" or "x" && (i + 2 >= s.Count || !s[i + 2].Vowel)) return false;
        }
        return true;
    }

    private static string Diphthong(Seg v, Seg glide)
    {
        if (v.Accent == Accent.Tilde || v.Nasal)
        {
            return (v.Base, glide.Base) switch
            {
                ('a', 'o') => "ɐ̃ʊ̃",
                ('a', 'e') => "ɐ̃y",
                ('o', 'e') => "õj",
                ('a', 'i') => "ɐ̃j",
                _ => v.Ph + "j",
            };
        }
        string g = glide.Base == 'u' ? "ʊ" : "ɪ";
        return (v.Ph, glide.Base) switch
        {
            ("a", 'i') => "I",
            ("a", 'u') => "W",
            ("e", 'i') => "A",
            ("o", 'u') => "ow",
            ("u", 'i') => "uɪ",
            ("i", 'u') => "iw",
            _ => v.Ph + g,
        };
    }

    private static string Quality(string w, List<Seg> s, int i, Seg v, bool stressed, bool postTonic, bool final)
    {
        bool beforeNasalCons = !v.CodaL && i + 1 < s.Count && s[i + 1].Ph is "m" or "n" or "ɲ" && i + 2 < s.Count && s[i + 2].Vowel;
        if (v.Accent == Accent.Tilde)
            return v.Base switch { 'a' => "ɐ̃", 'o' => "õ", _ => v.Base + "̃" };
        if (v.Nasal)
        {
            return v.Base switch
            {
                'a' => "ɐ̃",
                'e' => "A",
                'u' => "ũ",
                _ => v.Base.ToString(),
            };
        }
        switch (v.Base)
        {
            case 'a':
                if (stressed) return beforeNasalCons ? "ɐ̃" : "a";
                if (final || postTonic || beforeNasalCons) return "æ";
                return "a";
            case 'e':
                if (v.Accent == Accent.Acute) return "ɛ";
                if (v.Accent == Accent.Circumflex) return "e";
                if (stressed) return IsOpen(w, s, i) ? "ɛ" : "e";
                if (final) return "y";
                return "e";
            case 'o':
                if (v.Accent == Accent.Acute) return "ɔ";
                if (v.Accent == Accent.Circumflex) return "o";
                if (stressed) return IsOpen(w, s, i) ? "ɔ" : "o";
                if (final || (postTonic && i > 0 && s[i - 1].Vowel)) return "ʊ";
                return "o";
            default:
                return v.Base.ToString();
        }
    }

    private static bool IsOpen(string w, List<Seg> s, int i)
    {
        if (OpenVowel.Contains(w)) return true;
        Seg next = i + 1 < s.Count ? s[i + 1] : new Seg();
        // -el, -ol finales («papel», «farol»), -ela, -elo, -erra.
        if (s[i].CodaL) return true;
        if (next.Ph == "l" && i + 2 < s.Count && s[i + 2].Vowel && s[i + 2].Base is 'a' or 'o' && s[i].Base == 'e') return true;
        if (next.Ph == "x" && s[i].Base == 'e') return true;
        return false;
    }

    private static List<Seg> Letters(string w)
    {
        List<Seg> s = [];
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            char next = i + 1 < w.Length ? w[i + 1] : '\0';
            char after = i + 2 < w.Length ? w[i + 2] : '\0';
            char prev = i > 0 ? w[i - 1] : '\0';
            if (IsVowelLetter(c))
            {
                (char b, Accent a) = Split(c);
                Seg v = new() { Vowel = true, Base = b, Accent = a, Ph = b.ToString() };
                s.Add(v);
                // Vocal + m/n en coda: vocal nasal (la consonante se vuelve ŋ, m o nada).
                if (next is 'm' or 'n' && !(after == 'h') && !IsVowelLetter(after))
                {
                    v.Nasal = true;
                    if (after == '\0' || (after == 's' && i + 3 == w.Length))
                    {
                        // «-am» átono → ɐ̃ʊ̃; «-em», «-ens» → Aŋ; «-im», «-om», «-um» → iŋ, oŋ, ũŋ.
                        if (b == 'a' && next == 'm') s.Add(new Seg { Vowel = true, Base = 'o', Ph = "o", Glide = true, Nasal = true });
                        else v.NasalCoda = "ŋ";
                    }
                    else
                    {
                        v.NasalCoda = after is 'p' or 'b' ? "m" : "ŋ";
                    }
                    i++;
                    continue;
                }
                // l en coda: vocalizada.
                if (next == 'l' && after != 'h' && !IsVowelLetter(after))
                {
                    v.CodaL = true;
                    i++;
                }
                continue;
            }
            switch (c)
            {
                case 'b' or 'f' or 'k' or 'p' or 'v':
                    Add(c.ToString());
                    break;
                case 'd':
                    Add("d");
                    break;
                case 't':
                    if (next == 'c' && after == 'h') { Add("tʃ"); i += 2; }
                    else Add("t");
                    break;
                case 'c':
                    if (next == 'h') { Add("ʃ"); i++; }
                    else if (IsFront(next)) Add("s");
                    else Add("k");
                    break;
                case 'ç':
                    Add("s");
                    break;
                case 'g':
                    if (IsFront(next)) Add("ʒ");
                    else if (next == 'u' && IsFront(after) && SoundedU.Any(w.Contains)) { Add("ɡ"); Add("w"); i++; }
                    else if (next == 'u' && IsFront(after)) { Add("ɡ"); i++; }
                    else if (next == 'u' && after is 'a' or 'o') { Add("ɡ"); Add("w"); i++; }
                    else Add("ɡ");
                    break;
                case 'h':
                    break;
                case 'j':
                    Add("ʒ");
                    break;
                case 'l':
                    if (next == 'h') { Add("l"); Add("j"); i++; }
                    else Add("l");
                    break;
                case 'm':
                    Add("m");
                    break;
                case 'n':
                    if (next == 'h') { Add("ɲ"); i++; }
                    else Add("n");
                    break;
                case 'q':
                    Add("k");
                    if (next == 'u' && (after is 'a' or 'o' || SoundedU.Any(w.Contains))) { Add("w"); i++; }
                    else if (next == 'u') i++;
                    break;
                case 'r':
                    if (next == 'r') { Add("x"); i++; }
                    else if (i == 0 || prev is 'n' or 'l' or 's') Add("x");
                    else if (!IsVowelLetter(prev)) Add("r");
                    else if (next == '\0') Add("r");
                    else if (!IsVowelLetter(next)) Add("ɾə");
                    else Add("ɾ");
                    break;
                case 's':
                    if (next == 's') { Add("s"); i++; }
                    else if (next == 'c' && IsFront(after)) { Add("s"); i++; }
                    else if (next == 'ç') { Add("s"); i++; }
                    else if (i > 0 && IsVowelLetter(prev) && IsVowelLetter(next)) Add("z");
                    else if (i > 0 && next is 'b' or 'd' or 'g' or 'l' or 'm' or 'n' or 'r' or 'v' or 'j') Add("z");
                    else Add("s");
                    break;
                case 'x':
                    if (XAsKs.Contains(w)) { Add("k"); Add("s"); }
                    else if (XAsS.Contains(w)) Add("s");
                    else if (i == 0) Add("ʃ");
                    else if (prev == 'e' && i == 1 && IsVowelLetter(next)) Add("z");
                    else if (prev == 'e' && i == 1 && next == 'c' && IsFront(after)) { Add("s"); i++; }
                    else if (prev == 'e' && i == 1) Add("s");
                    else Add("ʃ");
                    break;
                case 'z':
                    Add(next == '\0' ? "s" : "z");
                    break;
                case 'w':
                    Add("w");
                    break;
                case 'y':
                    s.Add(new Seg { Vowel = true, Base = 'i', Ph = "i" });
                    break;
                default:
                    break;
            }
        }
        return s;

        void Add(string ph) => s.Add(new Seg { Ph = ph });
    }

    private static bool IsFront(char c) => c is 'e' or 'i' or 'é' or 'ê' or 'í' or 'y';

    private static bool IsVowelLetter(char c) => "aeiouáéíóúâêôãõàü".Contains(c, StringComparison.Ordinal);

    private static (char, Accent) Split(char c) => c switch
    {
        'á' => ('a', Accent.Acute), 'à' => ('a', Accent.None), 'â' => ('a', Accent.Circumflex), 'ã' => ('a', Accent.Tilde),
        'é' => ('e', Accent.Acute), 'ê' => ('e', Accent.Circumflex),
        'í' => ('i', Accent.Acute),
        'ó' => ('o', Accent.Acute), 'ô' => ('o', Accent.Circumflex), 'õ' => ('o', Accent.Tilde),
        'ú' => ('u', Accent.Acute), 'ü' => ('u', Accent.None),
        _ => (c, Accent.None),
    };
}
