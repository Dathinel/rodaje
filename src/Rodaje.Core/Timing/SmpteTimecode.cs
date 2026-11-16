namespace Rodaje.Core.Timing;

/// <summary>Código de tiempo SMPTE: cuadro absoluto, base nominal y drop frame (a 29,97 y 59,94). La presentación en pantalla es <c>Rodaje.Engine.Presentation.Timecode</c>.</summary>
public readonly record struct SmpteTimecode
{
    public long Frame { get; }
    public int Base { get; }
    public bool DropFrame { get; }

    public SmpteTimecode(long frame, int nominalBase, bool dropFrame)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "El cuadro no puede ser negativo.");
        if (!IsSupportedBase(nominalBase))
            throw new ArgumentOutOfRangeException(nameof(nominalBase), "La base nominal debe ser 24, 25, 30, 48, 50 o 60.");
        if (dropFrame && nominalBase is not (30 or 60))
            throw new ArgumentException("Drop frame solo admite las bases 30 y 60.", nameof(dropFrame));

        Frame = frame;
        Base = nominalBase;
        DropFrame = dropFrame;
    }

    public static int NominalBase(Rational rate)
    {
        if (TryGetNominalBase(rate, out int nominalBase))
            return nominalBase;
        throw new ArgumentOutOfRangeException(nameof(rate), "La tasa no corresponde a una base nominal admitida.");
    }

    public static bool CanDropFrame(Rational rate) => rate == Rational.Ntsc30 || rate == Rational.Ntsc60;

    public static SmpteTimecode Parse(string text, Rational rate)
    {
        int nominalBase = NominalBase(rate);
        if (text is { Length: 11 } && text[8] is ';' or '.' or ',' && !CanDropFrame(rate))
            throw new ArgumentException("La tasa no admite drop frame.", nameof(rate));
        if (TryParseCore(text, nominalBase, CanDropFrame(rate), out SmpteTimecode timecode))
            return timecode;
        throw new FormatException("El código de tiempo no es válido para la tasa indicada.");
    }

    public static bool TryParse(string? text, Rational rate, out SmpteTimecode timecode)
    {
        timecode = default;
        return TryGetNominalBase(rate, out int nominalBase)
            && TryParseCore(text, nominalBase, CanDropFrame(rate), out timecode);
    }

    public static SmpteTimecode FromFrame(long frame, Rational rate, bool dropFrame)
    {
        int nominalBase = NominalBase(rate);
        if (dropFrame && !CanDropFrame(rate))
            throw new ArgumentException("La tasa no admite drop frame.", nameof(dropFrame));
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), "El cuadro no puede ser negativo.");
        return new SmpteTimecode(frame % FramesPerDay(nominalBase, dropFrame), nominalBase, dropFrame);
    }

    public static SmpteTimecode FromTime(Time time, Rational rate, bool dropFrame) =>
        FromFrame(time.ToFrames(rate), rate, dropFrame);

    public Time ToTime(Rational rate) => Time.FromFrames(Frame, rate);

    public string Format()
    {
        if (!IsSupportedBase(Base))
            throw new InvalidOperationException("El código de tiempo no tiene una base nominal válida.");

        long label = Frame % FramesPerDay(Base, DropFrame);
        if (DropFrame)
        {
            int dropped = Base / 15;
            int perMinute = Base * 60 - dropped;
            int perTenMinutes = Base * 600 - dropped * 9;
            long remainder = label % perTenMinutes;
            // Cada bloque de diez minutos empieza con un minuto sin saltos.
            label += dropped * 9 * (label / perTenMinutes);
            if (remainder >= dropped)
                label += dropped * ((remainder - dropped) / perMinute);
        }

        return string.Create(11, (Label: (int)label, Base, DropFrame), static (span, state) =>
        {
            int seconds = state.Label / state.Base;
            WritePair(span, 0, seconds / 3600);
            span[2] = ':';
            WritePair(span, 3, seconds / 60 % 60);
            span[5] = ':';
            WritePair(span, 6, seconds % 60);
            span[8] = state.DropFrame ? ';' : ':';
            WritePair(span, 9, state.Label % state.Base);
        });
    }

    public override string ToString() => Format();

    public SmpteTimecode AddFrames(long frames) => new(checked(Frame + frames), Base, DropFrame);

    private static bool IsSupportedBase(int nominalBase) => nominalBase is 24 or 25 or 30 or 48 or 50 or 60;

    private static bool TryGetNominalBase(Rational rate, out int nominalBase)
    {
        nominalBase = 0;
        if (rate.Num <= 0 || rate.Den <= 0)
            return false;
        long rounded = Math.DivRem(rate.Num, rate.Den, out long remainder);
        if (rounded > 60)
            return false;
        // Compara con la mitad sin duplicar el resto, que podría desbordar.
        if (remainder >= rate.Den - remainder)
            rounded++;
        if (rounded > 60)
            return false;
        nominalBase = (int)rounded;
        return IsSupportedBase(nominalBase);
    }

    private static int FramesPerDay(int nominalBase, bool dropFrame) =>
        nominalBase * 86_400 - (dropFrame ? nominalBase / 15 * 1296 : 0);

    private static bool TryParseCore(string? text, int nominalBase, bool canDropFrame, out SmpteTimecode timecode)
    {
        timecode = default;
        if (text is not { Length: 11 } || text[2] != ':' || text[5] != ':')
            return false;
        bool dropFrame = text[8] is ';' or '.' or ',';
        if ((!dropFrame && text[8] != ':') || (dropFrame && !canDropFrame))
            return false;

        int hours = ReadPair(text, 0);
        int minutes = ReadPair(text, 3);
        int seconds = ReadPair(text, 6);
        int frames = ReadPair(text, 9);
        if ((uint)hours > 23 || (uint)minutes > 59 || (uint)seconds > 59 || (uint)frames >= nominalBase)
            return false;

        int totalMinutes = hours * 60 + minutes;
        int frame = (totalMinutes * 60 + seconds) * nominalBase + frames;
        if (dropFrame)
        {
            int dropped = nominalBase / 15;
            if (minutes % 10 != 0 && seconds == 0 && frames < dropped)
                return false;
            frame -= dropped * (totalMinutes - totalMinutes / 10);
        }

        timecode = new SmpteTimecode(frame, nominalBase, dropFrame);
        return true;
    }

    private static int ReadPair(string text, int offset)
    {
        int tens = text[offset] - '0';
        int units = text[offset + 1] - '0';
        return (uint)tens <= 9 && (uint)units <= 9 ? tens * 10 + units : -1;
    }

    private static void WritePair(Span<char> span, int offset, int value)
    {
        span[offset] = (char)('0' + value / 10);
        span[offset + 1] = (char)('0' + value % 10);
    }
}
