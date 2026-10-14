namespace Rodaje.Packages.Tests;

public sealed class PackageVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("1.0.0-99999999999999999999", "1.0.0-100000000000000000000")]
    [InlineData("1.0.0-9223372036854775807", "1.0.0-9223372036854775808")]
    [InlineData("1.0.0-99999999999999999999", "1.0.0-alpha")]
    public void Semver_order(string lower, string higher)
    {
        PackageVersion a = PackageVersion.Parse(lower);
        PackageVersion b = PackageVersion.Parse(higher);

        Assert.True(a < b);
        Assert.True(b > a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Build_metadata_does_not_count()
    {
        Assert.Equal(PackageVersion.Parse("1.2.3+abc"), PackageVersion.Parse("1.2.3+def"));
        Assert.Equal("1.2.3+abc", PackageVersion.Parse("1.2.3+abc").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.x")]
    public void Rejects_what_is_not_semver(string text) => Assert.False(PackageVersion.TryParse(text, out _));
}

public sealed class PackageReferenceTests
{
    [Fact]
    public void Round_trips()
    {
        Assert.True(PackageReference.TryParse("rodaje-titulos@2.1.0-beta/neon.suave", out PackageReference? reference));
        Assert.Equal("rodaje-titulos", reference.PackageId);
        Assert.Equal(PackageVersion.Parse("2.1.0-beta"), reference.Version);
        Assert.Equal("neon.suave", reference.ItemId);
        Assert.Equal("rodaje-titulos@2.1.0-beta/neon.suave", reference.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("paquete")]
    [InlineData("paquete@1.0.0")]
    [InlineData("paquete@1.0.0/")]
    [InlineData("@1.0.0/a")]
    [InlineData("Paquete@1.0.0/a")]
    [InlineData("paquete@1.0/a")]
    [InlineData("paquete/a@1.0.0")]
    public void Rejects_malformed_references(string text) => Assert.False(PackageReference.TryParse(text, out _));

    [Theory]
    [InlineData("rodaje", true)]
    [InlineData("rodaje-transiciones", true)]
    [InlineData("zoom.suave2", true)]
    [InlineData("", false)]
    [InlineData("a-", false)]
    [InlineData(".a", false)]
    [InlineData("a.-b", false)]
    [InlineData("ñandú", false)]
    [InlineData("A", false)]
    [InlineData("con", false)]
    [InlineData("nul", false)]
    [InlineData("aux.cosas", false)]
    [InlineData("com1", false)]
    [InlineData("lpt9.x", false)]
    [InlineData("consola", true)]
    [InlineData("com10", true)]
    public void Identifier_rules(string id, bool valid) => Assert.Equal(valid, Identifiers.IsValid(id));
}

public sealed class SafePathsTests
{
    [Theory]
    [InlineData("paquete.json", "paquete.json")]
    [InlineData("transiciones/deslizar.json", "transiciones/deslizar.json")]
    [InlineData("a\\b\\c.cube", "a/b/c.cube")]
    [InlineData("sonidos/golpe 1.wav", "sonidos/golpe 1.wav")]
    [InlineData("año/canción.ogg", "año/canción.ogg")]
    [InlineData("console.txt", "console.txt")]
    public void Accepts_plain_relative_paths(string relative, string expected)
    {
        Assert.True(SafePaths.TryNormalizeRelative(relative, out string? normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("./a.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\win.ini")]
    [InlineData("\\\\servidor\\recurso\\a.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:evil.txt")]
    [InlineData("a.txt:flujo")]
    [InlineData("a//b.txt")]
    [InlineData("carpeta/")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("sub/aux.json")]
    [InlineData("NUL.wav")]
    [InlineData("COM1")]
    [InlineData("lpt9.cube")]
    [InlineData("COM¹.txt")]
    [InlineData("punto.")]
    [InlineData("espacio ")]
    [InlineData(" inicio")]
    [InlineData("a?.txt")]
    [InlineData("a*.txt")]
    [InlineData("a<b>.txt")]
    [InlineData("a|b.txt")]
    [InlineData("comilla\".txt")]
    [InlineData("control\u0001.txt")]
    [InlineData("foto\u202Egnp.exe")]
    [InlineData("a\u2066b.txt")]
    public void Rejects_anything_that_could_escape_or_alias(string relative) =>
        Assert.False(SafePaths.TryNormalizeRelative(relative, out _));

    [Fact]
    public void Rejects_very_long_paths() =>
        Assert.False(SafePaths.TryNormalizeRelative(new string('a', SafePaths.MaxRelativeLength + 1), out _));

    [Fact]
    public void Inside_check_is_not_fooled_by_a_sibling_with_the_same_prefix()
    {
        string root = Path.Combine(Path.GetTempPath(), "paquetes", "a");
        Assert.True(SafePaths.IsInside(Path.Combine(root, "x.json"), root));
        Assert.False(SafePaths.IsInside(Path.Combine(Path.GetTempPath(), "paquetes", "ab", "x.json"), root));
        Assert.False(SafePaths.IsInside(root, root));
        Assert.False(SafePaths.IsInside(Path.Combine(root, "..", "b", "x.json"), root));
    }
}
