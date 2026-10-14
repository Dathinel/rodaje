namespace Rodaje.Core.Tests.Caza;

using Rodaje.Core.Model;
using Xunit;

public class MediaRefCazaTests
{
    [Hardened.WindowsOnlyFact("una ruta con unidad (C:) solo es absoluta en Windows")]
    public void ResolvePath_ResolvesWindowsPathWithLeadingBackslash()
    {
        var mediaRef = new MediaRef { Path = @"\Videos\a.mp4" };
        var resolved = mediaRef.ResolvePath(@"C:\Project");
        Assert.Equal(@"C:\Videos\a.mp4", resolved);
    }
}
