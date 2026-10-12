using System.Globalization;

namespace Rodaje.Packages;

/// <summary>Versión semántica <c>mayor.menor.parche[-previa][+compilación]</c>, comparada según SemVer 2.0.</summary>
public sealed record PackageVersion : IComparable<PackageVersion>
{
    private PackageVersion(int major, int minor, int patch, string? prerelease, string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Text = text;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? Prerelease { get; }

    /// <summary>Tal como se escribió (incluida la parte <c>+compilación</c>, que no cuenta al comparar).</summary>
    public string Text { get; }

    public static PackageVersion Parse(string text) =>
        TryParse(text, out PackageVersion? version) ? version : throw new FormatException($"«{text}» no es una versión mayor.menor.parche.");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PackageVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > 64)
        {
            return false;
        }

        string core = text;
        int plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (!ValidIdentifiers(core[(plus + 1)..], numericLeadingZeros: true))
            {
                return false;
            }

            core = core[..plus];
        }

        string? prerelease = null;
        int dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            if (!ValidIdentifiers(prerelease, numericLeadingZeros: false))
            {
                return false;
            }

            core = core[..dash];
        }

        string[] parts = core.Split('.');
        if (parts.Length != 3 || !TryNumber(parts[0], out int major) || !TryNumber(parts[1], out int minor) || !TryNumber(parts[2], out int patch))
        {
            return false;
        }

        version = new PackageVersion(major, minor, patch, prerelease, text);
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        return c != 0 ? c : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public bool Equals(PackageVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => Text;

    public static bool operator <(PackageVersion left, PackageVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(PackageVersion left, PackageVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(PackageVersion left, PackageVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PackageVersion left, PackageVersion right) => left.CompareTo(right) >= 0;

    private static bool TryNumber(string s, out int value)
    {
        value = 0;
        return s.Length > 0 && (s.Length == 1 || s[0] != '0') && s.All(char.IsAsciiDigit)
            && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool ValidIdentifiers(string s, bool numericLeadingZeros)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (string part in s.Split('.'))
        {
            if (part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }

            if (!numericLeadingZeros && part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static int ComparePrerelease(string? a, string? b)
    {
        if (a is null || b is null)
        {
            // Sin parte previa es mayor: 1.0.0-beta < 1.0.0.
            return a is null ? (b is null ? 0 : 1) : -1;
        }

        string[] x = a.Split('.');
        string[] y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xNum = long.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out long xn);
            bool yNum = long.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out long yn);
            int c = (xNum, yNum) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
