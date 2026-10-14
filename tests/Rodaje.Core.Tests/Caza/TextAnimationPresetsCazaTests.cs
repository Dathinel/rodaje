using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Core.Tests.Caza;

public class TextAnimationPresetsCazaTests
{
    [Fact]
    public void All_ContainsExactlyTenFactoryPresets()
    {
        Assert.Equal(10, TextAnimationPresets.All.Count);
        Assert.All(TextAnimationPresets.All, Assert.NotNull);
        Assert.Equal(10, TextAnimationPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.All(TextAnimationPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
        Assert.All(TextAnimationPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Out_ReversesEntryAnimation()
    {
        foreach (TextAnimationPreset preset in TextAnimationPresets.All)
        {
            StyleAnimation entry = preset.In();
            StyleAnimation exit = preset.Out();

            Assert.Equal(entry.DurationSeconds, exit.DurationSeconds);
            Assert.Equal(entry.Unit, exit.Unit);
            Assert.Equal(entry.Stagger, exit.Stagger);
            Assert.Equal(entry.Curves.Keys.OrderBy(k => k), exit.Curves.Keys.OrderBy(k => k));

            foreach (string property in entry.Curves.Keys)
            {
                List<StyleKey> entryKeys = entry.Curves[property];
                List<StyleKey> exitKeys = exit.Curves[property];

                Assert.Equal(entryKeys[0].Value, exitKeys[^1].Value);
                Assert.Equal(entryKeys[^1].Value, exitKeys[0].Value);
                Assert.Equal(0.0, exitKeys[0].T);
                Assert.Equal(exit.DurationSeconds, exitKeys[^1].T);
            }
        }
    }

    [Fact]
    public void Match_RecognizesFactoryPresets()
    {
        Assert.Null(TextAnimationPresets.Match(null, entry: true));
        Assert.Null(TextAnimationPresets.Match(null, entry: false));

        foreach (TextAnimationPreset preset in TextAnimationPresets.All)
        {
            StyleAnimation entry = preset.In();
            StyleAnimation exit = preset.Out();

            Assert.Same(preset, TextAnimationPresets.Match(entry, entry: true));
            Assert.Same(preset, TextAnimationPresets.Match(exit, entry: false));

            Assert.Null(TextAnimationPresets.Match(entry, entry: false));
            Assert.Null(TextAnimationPresets.Match(exit, entry: true));

            StyleAnimation customEntry = preset.In(seconds: 2.0);
            StyleAnimation customExit = preset.Out(seconds: 2.0);

            Assert.Same(preset, TextAnimationPresets.Match(customEntry, entry: true));
            Assert.Same(preset, TextAnimationPresets.Match(customExit, entry: false));
        }
    }
}
