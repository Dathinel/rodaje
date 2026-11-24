using Rodaje.Core.Collab;

namespace Rodaje.Core.Tests.Collab;

public sealed class ElementVersionsTests
{
    private static Patch PatchOf(params PatchOp[] ops) => new("p1", 0, "k:1", "prueba", ops, [], []);

    [Fact]
    public void Unknown_elements_are_at_version_zero_and_advance_by_one_per_accepted_patch()
    {
        var versions = new ElementVersions();
        var clip = new ElementKey(ElementKind.Clip, "c1");

        Assert.Equal(0, versions.Get(clip));

        versions.Advance(PatchOf(new PatchOp(ElementKind.Clip, "c1", "t1", 0, "{}")));
        versions.Advance(PatchOf(new PatchOp(ElementKind.Clip, "c1", "t1", 1, null)));

        Assert.Equal(2, versions.Get(clip));
        Assert.Equal(1, versions.Count);
    }

    [Fact]
    public void A_clone_is_independent()
    {
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Track, "t1"), 4);
        ElementVersions copy = versions.Clone();

        copy.Advance(PatchOf(new PatchOp(ElementKind.Track, "t1", "s1", 4, "{}")));

        Assert.Equal(4, versions.Get(ElementKind.Track, "t1"));
        Assert.Equal(5, copy.Get(ElementKind.Track, "t1"));
    }

    [Fact]
    public void Json_round_trips_in_a_fixed_order()
    {
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Media, "m1"), 2);
        versions.Set(new ElementKey(ElementKind.Clip, "b"), 7);
        versions.Set(new ElementKey(ElementKind.Clip, "a:b"), 1);

        string json = versions.ToJson();
        ElementVersions? back = ElementVersions.TryParse(json);

        Assert.NotNull(back);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(7, back.Get(ElementKind.Clip, "b"));
        Assert.Equal(1, back.Get(ElementKind.Clip, "a:b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"clip:\":1}")]
    [InlineData("{\":x\":1}")]
    [InlineData("{\"nube:x\":1}")]
    [InlineData("{\"clip:x\":-1}")]
    [InlineData("{\"clip:x\":\"1\"}")]
    [InlineData("{\"clip:x\":1.5}")]
    [InlineData("{\"clip:x\":99999999999}")]
    [InlineData("{\"clip:x\":")]
    public void Hostile_json_is_refused_without_throwing(string json) => Assert.Null(ElementVersions.TryParse(json));

    [Fact]
    public void Setting_zero_forgets_the_element()
    {
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Clip, "c"), 3);
        versions.Set(new ElementKey(ElementKind.Clip, "c"), 0);

        Assert.Equal(0, versions.Count);
    }
}
