using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

/// <summary>Lista de marcadores: renombrar y saltar al siguiente o al anterior.</summary>
public class MarkerListTests
{
    [Fact]
    public void RenameMarker_trims_and_rejects_empty_names()
    {
        var b = new PlacementBuilder();
        string id = TimelineEdits.AddMarker(b.Context(), F(10), "a");

        TimelineEdits.RenameMarker(b.Context(), id, "  Estribillo ");
        Assert.Equal("Estribillo", b.Sequence.Markers[0].Name);

        EditException empty = Assert.Throws<EditException>(() => TimelineEdits.RenameMarker(b.Context(), id, "   "));
        Assert.Contains("necesita un nombre", empty.Message, StringComparison.Ordinal);
        Assert.Equal("Estribillo", b.Sequence.Markers[0].Name);

        EditException missing = Assert.Throws<EditException>(() => TimelineEdits.RenameMarker(b.Context(), "nada", "x"));
        Assert.Contains("No existe el marcador «nada»", missing.Message, StringComparison.Ordinal);
        b.AssertValid();
    }

    [Fact]
    public void RenameMarker_command_undoes()
    {
        var b = new PlacementBuilder();
        string id = TimelineEdits.AddMarker(b.Context(), F(10), "a");
        var stack = new UndoStack(b.Project);

        stack.Execute(Edits.RenameMarker(b.Sequence.Id, id, "b"));
        Assert.Equal("b", b.Sequence.Markers[0].Name);
        Assert.True(stack.Undo());
        Assert.Equal("a", b.Sequence.Markers[0].Name);
        Assert.True(stack.Redo());
        Assert.Equal("b", b.Sequence.Markers[0].Name);
    }

    [Fact]
    public void Next_and_previous_marker_skip_the_one_under_the_cursor()
    {
        var b = new PlacementBuilder();
        TimelineEdits.AddMarker(b.Context(), F(10), "a");
        TimelineEdits.AddMarker(b.Context(), F(30), "b");
        TimelineEdits.AddMarker(b.Context(), F(20), "c");

        Assert.Equal("a", TimelineEdits.NextMarker(b.Sequence, F(0))?.Name);
        Assert.Equal("c", TimelineEdits.NextMarker(b.Sequence, F(10))?.Name);
        Assert.Null(TimelineEdits.NextMarker(b.Sequence, F(30)));
        Assert.Equal("c", TimelineEdits.PreviousMarker(b.Sequence, F(30))?.Name);
        Assert.Equal("b", TimelineEdits.PreviousMarker(b.Sequence, F(99))?.Name);
        Assert.Null(TimelineEdits.PreviousMarker(b.Sequence, F(10)));
        Assert.Null(TimelineEdits.NextMarker(new Sequence(), F(0)));
    }
}
