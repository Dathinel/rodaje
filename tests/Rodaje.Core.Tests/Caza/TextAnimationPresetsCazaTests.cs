using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Core.Tests.Caza;

public class TextAnimationPresetsCazaTests
{
    [Fact]
    public void All_ContainsExactlyTwelveFactoryPresets()
    {
        Assert.Equal(12, TextAnimationPresets.All.Count);
        Assert.All(TextAnimationPresets.All, Assert.NotNull);
        Assert.Equal(12, TextAnimationPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.All(TextAnimationPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
        Assert.All(TextAnimationPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Theory]
    [InlineData("tambaleo")]
    [InlineData("temblor")]
    public void Wobble_presets_settle_to_rest_with_shrinking_swings(string id)
    {
        StyleAnimation animation = TextAnimationPresets.Find(id)!.In();
        foreach ((string name, List<StyleKey> keys) in animation.Curves.Where(c => c.Key != "opacity"))
        {
            Assert.True(keys.Count >= 7, name);
            Assert.Equal(0, keys[^1].Value);
            Assert.Equal(animation.DurationSeconds, keys[^1].T, 9);
            Assert.True(keys.Zip(keys.Skip(1)).All(p => p.First.T < p.Second.T), name);
            Assert.True(Math.Abs(keys[1].Value) <= Math.Abs(keys[0].Value) * 1.0001 + 1e-9 || id == "temblor", name);
        }
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
