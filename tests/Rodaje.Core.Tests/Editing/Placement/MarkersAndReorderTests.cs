using System.Globalization;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

public class MarkersAndReorderTests
{
    private static string Markers(PlacementBuilder b) =>
        string.Join(" ", b.Sequence.Markers.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Name}@{m.Time.Ticks / FrameTicks}")));

    private static string Regions(PlacementBuilder b) =>
        string.Join(" ", b.Sequence.Regions.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Name}[{r.Start.Ticks / FrameTicks},{r.End.Ticks / FrameTicks})")));

    private static void Fails(PlacementBuilder b, Action action, string expectedMessagePart)
    {
        string before = b.Snapshot() + Markers(b) + Regions(b);
        EditException ex = Assert.Throws<EditException>(action);
        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot() + Markers(b) + Regions(b));
    }

    [Fact]
    public void AddMarker_keeps_markers_sorted_and_returns_new_id()
    {
        var b = new PlacementBuilder();
        string first = TimelineEdits.AddMarker(b.Context(), F(30), "b");
        string second = TimelineEdits.AddMarker(b.Context(), F(10), "a");
        TimelineEdits.AddMarker(b.Context(), F(30), "c");

        Assert.Equal("a@10 b@30 c@30", Markers(b));
        Assert.NotEqual(first, second);
        Assert.StartsWith("marker", first, StringComparison.Ordinal);
        b.AssertValid();
    }

    [Fact]
    public void MoveMarker_and_RemoveMarker()
    {
        var b = new PlacementBuilder();
        string a = TimelineEdits.AddMarker(b.Context(), F(10), "a");
        TimelineEdits.AddMarker(b.Context(), F(20), "b");

        TimelineEdits.MoveMarker(b.Context(), a, F(25));
        Assert.Equal("b@20 a@25", Markers(b));

        TimelineEdits.RemoveMarker(b.Context(), a);
        Assert.Equal("b@20", Markers(b));
        b.AssertValid();
    }

    [Fact]
    public void Marker_errors()
    {
        var b = new PlacementBuilder();
        string a = TimelineEdits.AddMarker(b.Context(), F(10), "a");
        Fails(b, () => TimelineEdits.AddMarker(b.Context(), F(-1), "x"), "antes del inicio");
        Fails(b, () => TimelineEdits.MoveMarker(b.Context(), a, F(-1)), "antes del inicio");
        Fails(b, () => TimelineEdits.MoveMarker(b.Context(), "nada", F(1)), "No existe el marcador «nada»");
        Fails(b, () => TimelineEdits.RemoveMarker(b.Context(), "nada"), "No existe el marcador «nada»");
    }

    [Fact]
    public void Regions_add_sorted_remove_and_errors()
    {
        var b = new PlacementBuilder();
        string late = TimelineEdits.AddRegion(b.Context(), F(20), F(40), "tarde");
        TimelineEdits.AddRegion(b.Context(), F(0), F(30), "pronto");
        Assert.Equal("pronto[0,30) tarde[20,40)", Regions(b));

        TimelineEdits.RemoveRegion(b.Context(), late);
        Assert.Equal("pronto[0,30)", Regions(b));
        b.AssertValid();

        Fails(b, () => TimelineEdits.AddRegion(b.Context(), F(5), F(5), "x"), "terminar después de empezar");
        Fails(b, () => TimelineEdits.AddRegion(b.Context(), F(-2), F(5), "x"), "antes del inicio");
        Fails(b, () => TimelineEdits.RemoveRegion(b.Context(), "nada"), "No existe la región «nada»");
    }

    [Fact]
    public void RippleDelete_with_markers_scope_moves_markers_and_regions()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);
        b.Add("v1", "c3", 20, 0, 10);
        TimelineEdits.AddMarker(b.Context(), F(5), "antes");
        TimelineEdits.AddMarker(b.Context(), F(14), "dentro");
        TimelineEdits.AddMarker(b.Context(), F(25), "despues");
        TimelineEdits.AddRegion(b.Context(), F(5), F(25), "cruza");
        TimelineEdits.AddRegion(b.Context(), F(11), F(19), "quitada");
        TimelineEdits.AddRegion(b.Context(), F(22), F(28), "luego");

        TimelineEdits.RippleDelete(b.Context(), ["c2"], RippleScope.AllTracksAndMarkers);

        Assert.Equal("c1@0[0,10) c3@10[0,10)", b.Layout("v1"));
        Assert.Equal("antes@5 dentro@10 despues@15", Markers(b));
        Assert.Equal("cruza[5,15) luego[12,18)", Regions(b));
        b.AssertValid();
    }

    [Theory]
    [InlineData(RippleScope.Track)]
    [InlineData(RippleScope.AllTracks)]
    public void Other_scopes_leave_markers_alone(RippleScope scope)
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 10, 0, 10);
        TimelineEdits.AddMarker(b.Context(), F(15), "m");
        TimelineEdits.AddRegion(b.Context(), F(12), F(18), "r");

        TimelineEdits.RippleDelete(b.Context(), ["c1"], scope);

        Assert.Equal("m@15", Markers(b));
        Assert.Equal("r[12,18)", Regions(b));
    }

    [Fact]
    public void RippleTrim_with_markers_scope_moves_markers_after_the_clip()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 20);
        b.Add("v1", "c2", 20, 0, 10);
        b.Add("v2", "o", 25, 0, 10);
        TimelineEdits.AddMarker(b.Context(), F(5), "dentro");
        TimelineEdits.AddMarker(b.Context(), F(22), "despues");

        TimelineEdits.RippleTrim(b.Context(), "c1", TrimEdge.Out, F(5), RippleScope.AllTracksAndMarkers);

        Assert.Equal("c1@0[0,25) c2@25[0,10)", b.Layout("v1"));
        Assert.Equal("o@30[0,10)", b.Layout("v2"));
        Assert.Equal("dentro@5 despues@27", Markers(b));
        b.AssertValid();
    }

    [Fact]
    public void CloseGap_with_markers_scope_moves_markers()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 20, 0, 10);
        TimelineEdits.AddMarker(b.Context(), F(25), "m");

        TimelineEdits.CloseGap(b.Context(), "v1", F(15), RippleScope.AllTracksAndMarkers);

        Assert.Equal("c2@10[0,10)", b.Layout("v1").Split(' ')[1]);
        Assert.Equal("m@15", Markers(b));
    }

    [Fact]
    public void Reorder_moves_clip_and_packs_from_first_start()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "a", 5, 0, 10);
        b.Add("v1", "b", 15, 0, 20);
        b.Add("v1", "c", 40, 0, 5);

        TimelineEdits.ReorderOnMainTrack(b.Context(), "c", 0);

        Assert.Equal("c@5[0,5) a@10[0,10) b@20[0,20)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Reorder_to_end_and_same_index_packs_only()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "a", 0, 0, 10);
        b.Add("v1", "b", 10, 0, 10);
        b.Add("v1", "c", 25, 0, 10);

        TimelineEdits.ReorderOnMainTrack(b.Context(), "a", 2);
        Assert.Equal("b@0[0,10) c@10[0,10) a@20[0,10)", b.Layout("v1"));

        TimelineEdits.ReorderOnMainTrack(b.Context(), "c", 1);
        Assert.Equal("b@0[0,10) c@10[0,10) a@20[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void Reorder_moves_linked_partners_by_the_same_amount()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "va", 0, 0, 10, link: "LA");
        b.Add("a1", "aa", 0, 0, 10, link: "LA");
        b.Add("v1", "vb", 10, 0, 20, link: "LB");
        b.Add("a1", "ab", 10, 0, 20, link: "LB");
        b.Add("a2", "music", 0, 0, 30, media: "aud");

        TimelineEdits.ReorderOnMainTrack(b.Context(), "vb", 0);

        Assert.Equal("vb@0[0,20) va@20[0,10)", b.Layout("v1"));
        Assert.Equal("ab@0[0,20) aa@20[0,10)", b.Layout("a1"));
        Assert.Equal("music@0[0,30)", b.Layout("a2"));
        b.AssertValid();
    }

    [Fact]
    public void Reorder_errors()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "va", 0, 0, 10, link: "L");
        b.Add("a1", "aa", 0, 0, 10, link: "L");
        b.Add("v1", "vb", 10, 0, 10);
        b.Add("a1", "blocker", 10, 0, 10);

        Fails(b, () => TimelineEdits.ReorderOnMainTrack(b.Context(), "vb", 2), "La posición 2 no existe");
        Fails(b, () => TimelineEdits.ReorderOnMainTrack(b.Context(), "nada", 0), "No existe el clip «nada»");

        // El audio enlazado de «va» caería sobre «blocker».
        Fails(b, () => TimelineEdits.ReorderOnMainTrack(b.Context(), "vb", 0), "se solapa");

        b.Track("a1").Locked = true;
        Fails(b, () => TimelineEdits.ReorderOnMainTrack(b.Context(), "vb", 0), "«Audio 1» está bloqueada");
        b.Track("a1").Locked = false;
        b.Track("v1").Locked = true;
        Fails(b, () => TimelineEdits.ReorderOnMainTrack(b.Context(), "vb", 0), "«Video 1» está bloqueada");
    }

    [Fact]
    public void Commands_undo_markers_and_merge_drags()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "a", 0, 0, 10);
        b.Add("v1", "c", 10, 0, 10);
        var stack = new UndoStack(b.Project);

        SequenceEditCommand add = Edits.AddMarker("secuencia", F(3), "uno", b.Ids);
        stack.Execute(add);
        string markerId = Assert.Single(add.CreatedIds);
        stack.Execute(Edits.MoveMarker("secuencia", markerId, F(4), b.Ids));
        stack.Execute(Edits.MoveMarker("secuencia", markerId, F(6), b.Ids));
        Assert.Equal(2, stack.Count);
        Assert.Equal("uno@6", Markers(b));

        SequenceEditCommand region = Edits.AddRegion("secuencia", F(2), F(8), "zona", b.Ids);
        stack.Execute(region);
        Assert.Equal("Añadir región", region.Description);
        stack.Execute(Edits.RemoveRegion("secuencia", Assert.Single(region.CreatedIds), b.Ids));
        stack.Execute(Edits.RemoveMarker("secuencia", markerId, b.Ids));
        Assert.Equal("", Markers(b) + Regions(b));

        stack.Undo();
        stack.Undo();
        Assert.Equal("uno@6", Markers(b));
        Assert.Equal("zona[2,8)", Regions(b));
        stack.Undo();
        stack.Undo();
        Assert.Equal("uno@3", Markers(b));

        stack.Execute(Edits.ReorderOnMainTrack("secuencia", "c", 0, b.Ids));
        stack.Execute(Edits.ReorderOnMainTrack("secuencia", "c", 1, b.Ids));
        stack.Execute(Edits.ReorderOnMainTrack("secuencia", "c", 0, b.Ids));
        Assert.Equal("Reordenar clip", stack.UndoDescription);
        Assert.Equal("c@0[0,10) a@10[0,10)", b.Layout("v1"));
        stack.Undo();
        Assert.Equal("a@0[0,10) c@10[0,10)", b.Layout("v1"));
    }

    [Fact]
    public void Validator_reports_bad_markers_and_regions()
    {
        var b = new PlacementBuilder();
        b.Sequence.Markers.Add(new Marker { Id = "m", Time = F(-1) });
        b.Sequence.Markers.Add(new Marker { Id = "m", Time = F(1) });
        b.Sequence.Regions.Add(new Region { Id = "r", Start = F(-1), End = F(-1) });
        b.Sequence.Regions.Add(new Region { Id = "", Start = F(0), End = F(1) });

        Assert.Equal(
            [
                "El marcador «m» está antes del inicio de la línea de tiempo.",
                "El identificador «m» está repetido (un marcador).",
                "La región «r» empieza antes del inicio de la línea de tiempo.",
                "La región «r» tiene duración cero o negativa.",
                "Hay una región sin identificador.",
            ],
            ModelValidator.Validate(b.Project));
    }

    [Fact]
    public void Clone_copies_markers_deeply()
    {
        var b = new PlacementBuilder();
        TimelineEdits.AddMarker(b.Context(), F(1), "m");
        TimelineEdits.AddRegion(b.Context(), F(1), F(2), "r");

        Sequence copy = b.Sequence.Clone();
        copy.Markers[0].Time = F(9);
        copy.Regions[0].Name = "otra";

        Assert.Equal("m@1", Markers(b));
        Assert.Equal("r[1,2)", Regions(b));
    }
}
