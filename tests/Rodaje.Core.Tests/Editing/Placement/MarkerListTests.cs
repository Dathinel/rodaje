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

    [Fact]
    public void EditMarker_sets_color_and_note_and_undoes_in_one_step()
    {
        var b = new PlacementBuilder();
        string id = TimelineEdits.AddMarker(b.Context(), F(10), "a");
        var stack = new UndoStack(b.Project);

        stack.Execute(Edits.EditMarker(b.Sequence.Id, id, " Toma buena ", "#e5484d", "  Revisar el audio  "));
        Marker marker = b.Sequence.Markers[0];
        Assert.Equal(("Toma buena", "#E5484D", "Revisar el audio"), (marker.Name, marker.Color, marker.Note));

        stack.Execute(Edits.EditMarker(b.Sequence.Id, id, "", null, "   "));
        Assert.Equal(("", (string?)null, (string?)null), (marker.Name, marker.Color, marker.Note));
        Assert.True(stack.Undo());
        Assert.Equal("#E5484D", b.Sequence.Markers[0].Color);

        Assert.Throws<EditException>(() => TimelineEdits.EditMarker(b.Context(), id, "x", "rojo", null));
        Assert.Throws<EditException>(() => TimelineEdits.EditMarker(b.Context(), id, "x", null, new string('n', MarkerStyle.MaxNoteLength + 1)));
        b.AssertValid();
    }

    [Fact]
    public void EditRegion_changes_details_and_span_and_keeps_regions_sorted()
    {
        var b = new PlacementBuilder();
        string first = TimelineEdits.AddRegion(b.Context(), F(0), F(10), "Intro");
        TimelineEdits.AddRegion(b.Context(), F(20), F(30), "Final");

        TimelineEdits.EditRegion(b.Context(), first, "Cierre", "#46A758", "Música más baja", F(40), F(50));
        Assert.Equal(["Final", "Cierre"], b.Sequence.Regions.Select(r => r.Name));
        Region region = b.Sequence.Regions[1];
        Assert.Equal((F(40), F(50), "#46A758", "Música más baja"), (region.Start, region.End, region.Color, region.Note));
        Assert.Throws<EditException>(() => TimelineEdits.EditRegion(b.Context(), first, "x", null, null, F(5), F(5)));
        b.AssertValid();
    }

    [Fact]
    public void Color_and_note_are_saved_only_when_set_and_validated_on_load()
    {
        var b = new PlacementBuilder();
        string plain = TimelineEdits.AddMarker(b.Context(), F(5), "Sin color");
        string marked = TimelineEdits.AddMarker(b.Context(), F(10), "Con color");
        TimelineEdits.EditMarker(b.Context(), marked, "Con color", "#3E63DD", "Línea 1\nLínea 2 «ñ»");
        string region = TimelineEdits.AddRegion(b.Context(), F(0), F(20), "Tramo");
        TimelineEdits.EditRegion(b.Context(), region, "Tramo", "#FFC53D", null, F(0), F(20));

        string json = Rodaje.Core.Serialization.ProjectSerializer.Serialize(b.Project);
        Project back = Rodaje.Core.Serialization.ProjectSerializer.Deserialize(json);
        Sequence sequence = back.Sequences[0];
        Assert.Equal((null, null), (sequence.Markers.Find(m => m.Id == plain)!.Color, sequence.Markers.Find(m => m.Id == plain)!.Note));
        Assert.Equal(("#3E63DD", "Línea 1\nLínea 2 «ñ»"), (sequence.Markers.Find(m => m.Id == marked)!.Color, sequence.Markers.Find(m => m.Id == marked)!.Note));
        Assert.Equal("#FFC53D", sequence.Regions[0].Color);
        Assert.Null(sequence.Regions[0].Note);
        Assert.Equal(2, json.Split("\"color\"").Length - 1);

        sequence.Markers[0].Color = "azul";
        Assert.Contains(ModelValidator.Validate(back), e => e.Contains("no es #RRGGBB", StringComparison.Ordinal));
    }
}
