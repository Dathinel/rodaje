using Rodaje.Core.Collab;

namespace Rodaje.Core.Tests.Collab;

public sealed class PatchDiffTests
{
    private static readonly PatchHeader Header = new("p1", 42, "k:1", "Editar");

    private static SharedState State(
        IEnumerable<KeyValuePair<ElementKey, ElementState>>? elements = null,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? orders = null) =>
        new("project", elements?.ToDictionary() ?? [], orders?.ToDictionary() ?? []);

    private static KeyValuePair<ElementKey, ElementState> Element(
        ElementKind kind, string id, string? parent = null, string content = "{}") =>
        new(new ElementKey(kind, id), new ElementState(parent, content));

    private static KeyValuePair<string, IReadOnlyList<string>> Order(string container, params string[] ids) => new(container, ids);

    private static Patch Create(SharedState before, SharedState after, ElementVersions? versions = null) =>
        Assert.IsType<Patch>(PatchDiff.Create(before, after, versions ?? new ElementVersions(), Header));

    private static void AssertSamePatch(Patch expected, Patch actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.BaseOrder, actual.BaseOrder);
        Assert.Equal(expected.Author, actual.Author);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Ops, actual.Ops);
        Assert.Equal(expected.Orders.Select(o => o.Container), actual.Orders.Select(o => o.Container));
        for (int i = 0; i < expected.Orders.Count; i++)
        {
            Assert.Equal(expected.Orders[i].Ids, actual.Orders[i].Ids);
        }

        Assert.Equal(expected.Blobs, actual.Blobs);
    }

    [Fact]
    public void Equal_states_return_null()
    {
        Assert.Null(PatchDiff.Create(State(), State(), new ElementVersions(), Header));
        SharedState before = State([Element(ElementKind.Media, "m1")], [Order(PatchContainers.Media, "m1")]);
        SharedState after = State([Element(ElementKind.Media, "m1")], [Order(PatchContainers.Media, "m1")]);

        Assert.Null(PatchDiff.Create(before, after, new ElementVersions(), Header));
    }

    [Theory]
    [InlineData(false, true, null, "t1", "{}", "{}")]
    [InlineData(true, true, "t1", "t1", "{}", "{\"name\":\"A\"}")]
    [InlineData(true, true, "t1", "t2", "{}", "{}")]
    [InlineData(true, true, "t1", "T1", "{}", "{}")]
    [InlineData(true, true, "t1", "t1", "{\"name\":\"a\"}", "{\"name\":\"A\"}")]
    [InlineData(true, false, "t1", null, "{}", "{}")]
    public void Element_changes_keep_the_correct_parent_content_and_header(
        bool existed, bool remains, string? oldParent, string? newParent, string oldContent, string newContent)
    {
        SharedState before = State(existed ? [Element(ElementKind.Clip, "c1", oldParent, oldContent)] : []);
        SharedState after = State(remains ? [Element(ElementKind.Clip, "c1", newParent, newContent)] : []);

        Patch patch = Create(before, after);

        Assert.Equal(new PatchOp(ElementKind.Clip, "c1", remains ? newParent : oldParent, 0, remains ? newContent : null),
            Assert.Single(patch.Ops));
        Assert.Equal(Header.Id, patch.Id);
        Assert.Equal(Header.BaseOrder, patch.BaseOrder);
        Assert.Equal(Header.Author, patch.Author);
        Assert.Equal(Header.Description, patch.Description);
        Assert.Empty(patch.Orders);
        Assert.Empty(patch.Blobs);
    }

    [Fact]
    public void Versions_include_deletions_and_recreated_tombstones_without_advancing()
    {
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Media, "changed"), 3);
        versions.Set(new ElementKey(ElementKind.Media, "deleted"), 5);
        versions.Advance(new Patch("old", 0, "k:1", "Borrar",
            [new PatchOp(ElementKind.Media, "revived", null, 7, null)], [], []));
        string original = versions.ToJson();
        SharedState before = State([Element(ElementKind.Media, "changed"), Element(ElementKind.Media, "deleted")]);
        SharedState after = State([Element(ElementKind.Media, "changed", content: "{\"x\":1}"), Element(ElementKind.Media, "revived")]);

        Patch patch = Create(before, after, versions);

        Assert.Equal(5, patch.Ops.Single(o => o.Id == "deleted").BaseVersion);
        Assert.Equal(3, patch.Ops.Single(o => o.Id == "changed").BaseVersion);
        Assert.Equal(8, patch.Ops.Single(o => o.Id == "revived").BaseVersion);
        Assert.Equal(original, versions.ToJson());
    }

    [Fact]
    public void Operations_are_canonical_and_independent_of_dictionary_insertion_order()
    {
        ElementKind[] deletes = [ElementKind.Clip, ElementKind.Marker, ElementKind.Region, ElementKind.Track,
            ElementKind.Sequence, ElementKind.Transcript, ElementKind.Media];
        ElementKind[] upserts = [ElementKind.Media, ElementKind.Transcript, ElementKind.Sequence, ElementKind.Track,
            ElementKind.Marker, ElementKind.Region, ElementKind.Clip];
        string[] ids = ["a", "Z", "A", "á"];
        var beforeElements = new List<KeyValuePair<ElementKey, ElementState>>();
        var afterElements = new List<KeyValuePair<ElementKey, ElementState>>();
        foreach (ElementKind kind in Enum.GetValues<ElementKind>())
        {
            foreach (string id in ids)
            {
                beforeElements.Add(Element(kind, $"{kind}-delete-{id}"));
                beforeElements.Add(Element(kind, $"{kind}-upsert-{id}"));
                afterElements.Add(Element(kind, $"{kind}-upsert-{id}", content: "{\"x\":1}"));
                afterElements.Add(Element(kind, $"{kind}-new-{id}"));
            }
        }

        KeyValuePair<string, IReadOnlyList<string>>[] orders =
        [Order(PatchContainers.Sequences, "Sequence-new-á", "Sequence-new-a", "Sequence-new-Z", "Sequence-new-A"),
            Order(PatchContainers.Media, "Media-new-á", "Media-new-a", "Media-new-Z", "Media-new-A")];
        SharedState before = State(beforeElements);
        SharedState after = State(afterElements, orders);
        Patch first = Create(before, after);

        PatchOp[] expected = deletes.SelectMany(kind => ids.Order(StringComparer.Ordinal)
                .Select(id => new PatchOp(kind, $"{kind}-delete-{id}", null, 0, null)))
            .Concat(upserts.SelectMany(kind => new[] { "new", "upsert" }.SelectMany(action => ids.Order(StringComparer.Ordinal)
                .Select(id => new PatchOp(kind, $"{kind}-{action}-{id}", null, 0, action == "new" ? "{}" : "{\"x\":1}")))))
            .ToArray();
        Assert.Equal(expected, first.Ops);
        Assert.Equal(new[] { PatchContainers.Media, PatchContainers.Sequences }, first.Orders.Select(o => o.Container));
        AssertSamePatch(first, Create(before, after));
        AssertSamePatch(first, Create(State(beforeElements.AsEnumerable().Reverse()), State(afterElements.AsEnumerable().Reverse(), orders.Reverse())));
        Assert.Equal(beforeElements, before.Elements);
        Assert.Equal(afterElements, after.Elements);
        Assert.Equal(orders, after.Orders);
    }

    [Fact]
    public void New_elements_appended_in_operation_order_need_no_order()
    {
        SharedState before = State([Element(ElementKind.Media, "m0")], [Order(PatchContainers.Media, "m0")]);
        SharedState after = State([Element(ElementKind.Media, "m2"), Element(ElementKind.Media, "m0"), Element(ElementKind.Media, "m1")],
            [Order(PatchContainers.Media, "m0", "m1", "m2")]);

        Assert.Empty(Create(before, after).Orders);
        Assert.Empty(Create(State(), State([Element(ElementKind.Media, "m2"), Element(ElementKind.Media, "m1")],
            [Order(PatchContainers.Media, "m1", "m2")])).Orders);
    }

    [Fact]
    public void Reordering_without_element_changes_emits_only_an_order()
    {
        KeyValuePair<ElementKey, ElementState>[] elements = [Element(ElementKind.Media, "m1"), Element(ElementKind.Media, "m2")];
        Patch patch = Create(State(elements, [Order(PatchContainers.Media, "m1", "m2")]),
            State(elements, [Order(PatchContainers.Media, "m2", "m1")]));

        Assert.Empty(patch.Ops);
        Assert.Equal(PatchContainers.Media, Assert.Single(patch.Orders).Container);
        Assert.Equal(new[] { "m2", "m1" }, patch.Orders[0].Ids);
    }

    [Fact]
    public void Moving_an_existing_element_appends_it_to_its_new_container()
    {
        string source = PatchContainers.VideoTracks("s1");
        string target = PatchContainers.VideoTracks("s2");
        KeyValuePair<ElementKey, ElementState>[] common = [Element(ElementKind.Sequence, "s1"), Element(ElementKind.Sequence, "s2")];
        SharedState before = State(common.Concat([Element(ElementKind.Track, "t1", "s1"), Element(ElementKind.Track, "t2", "s2")]),
            [Order(source, "t1"), Order(target, "t2")]);
        SharedState after = State(common.Concat([Element(ElementKind.Track, "t1", "s2"), Element(ElementKind.Track, "t2", "s2")]),
            [Order(source), Order(target, "t1", "t2")]);

        Patch patch = Create(before, after);

        Assert.Equal("t1", Assert.Single(patch.Ops).Id);
        Assert.Equal(target, Assert.Single(patch.Orders).Container);
        Assert.Equal(new[] { "t1", "t2" }, patch.Orders[0].Ids);
    }

    [Fact]
    public void Deletions_and_removed_containers_need_no_order()
    {
        SharedState before = State([Element(ElementKind.Media, "m1"), Element(ElementKind.Media, "m2")],
            [Order(PatchContainers.Media, "m1", "m2"), Order(PatchContainers.VideoTracks("s1"))]);
        SharedState after = State([Element(ElementKind.Media, "m2")], [Order(PatchContainers.Media, "m2")]);

        Assert.Empty(Create(before, after).Orders);
        Assert.Null(PatchDiff.Create(State(orders: [Order(PatchContainers.Media)]), State(), new ElementVersions(), Header));
    }

    [Fact]
    public void A_new_container_with_a_different_order_than_its_upserts_emits_an_order()
    {
        Patch patch = Create(State(), State([Element(ElementKind.Media, "m1"), Element(ElementKind.Media, "m2")],
            [Order(PatchContainers.Media, "m2", "m1")]));

        Assert.Equal(PatchContainers.Media, Assert.Single(patch.Orders).Container);
        Assert.Equal(new[] { "m2", "m1" }, patch.Orders[0].Ids);
    }

    [Fact]
    public void An_existing_id_without_an_operation_is_not_implicitly_appended()
    {
        KeyValuePair<ElementKey, ElementState>[] elements = [Element(ElementKind.Media, "m1")];
        Patch patch = Create(State(elements), State(elements, [Order(PatchContainers.Media, "m1")]));

        Assert.Empty(patch.Ops);
        Assert.Equal(new[] { "m1" }, Assert.Single(patch.Orders).Ids);
    }

    [Fact]
    public void Valid_state_combinations_do_not_throw()
    {
        SharedState[] states = [State(), State(orders: [Order(PatchContainers.Media)]),
            State([Element(ElementKind.Media, "m1")], [Order(PatchContainers.Media, "m1")]),
            State([Element(ElementKind.Media, "m2"), Element(ElementKind.Media, "m1", content: "{\"name\":\"ñ\"}")],
                [Order(PatchContainers.Media, "m2", "m1")])];
        foreach (SharedState before in states)
        {
            foreach (SharedState after in states)
            {
                Assert.Null(Record.Exception(() => PatchDiff.Create(before, after, new ElementVersions(), Header)));
            }
        }
    }

    [Fact]
    public void Null_arguments_are_rejected_by_name()
    {
        SharedState state = State();
        var versions = new ElementVersions();

        Assert.Throws<ArgumentNullException>("before", () => PatchDiff.Create(null!, state, versions, Header));
        Assert.Throws<ArgumentNullException>("after", () => PatchDiff.Create(state, null!, versions, Header));
        Assert.Throws<ArgumentNullException>("versions", () => PatchDiff.Create(state, state, null!, Header));
        Assert.Throws<ArgumentNullException>("header", () => PatchDiff.Create(state, state, versions, null!));
    }
}
