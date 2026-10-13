using System.Collections;
using Rodaje.App.Views.Library;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// La lista de la biblioteca: cada medio visible seguido de sus subclips. Antes, por cada medio se filtraba la lista entera
/// de subclips (<c>Subclips.Where(s =&gt; s.MediaId == …)</c>), medios × subclips en cada reconstrucción de la lista; ahora se
/// agrupan una vez por lista con <see cref="SubclipsByMedia"/>.
/// </summary>
public sealed class SubclipsByMediaTests
{
    private sealed record Media(string Id, bool Shown = true);

    private static Subclip Clip(string id, string mediaId) => new(id, mediaId, id, Time.Zero, Time.FromSeconds(1));

    [Fact]
    public void EachMediaIsFollowedByItsSubclips_InListOrder()
    {
        Media[] media = [new("m2"), new("m1"), new("m3", Shown: false), new("m4")];
        Subclip[] subclips = [Clip("a", "m1"), Clip("b", "m2"), Clip("c", "m1"), Clip("d", "m3"), Clip("e", "fuera")];

        List<string> items = SubclipsByMedia.Interleave(media, m => m.Id, subclips,
            m => m.Shown ? m.Id : null, (s, m) => $"{m.Id}/{s.Id}");

        // m3 no se muestra y se salta con su subclip; el subclip de un medio que no está en la lista no aparece.
        Assert.Equal(["m2", "m2/b", "m1", "m1/a", "m1/c", "m4"], items);
    }

    [Fact]
    public void Of_GivesTheSubclipsOfAMediaOrEmpty()
    {
        var groups = new SubclipsByMedia([Clip("a", "m1"), Clip("b", "m2"), Clip("c", "m1")]);

        Assert.Equal(["a", "c"], groups.Of("m1").Select(s => s.Id));
        Assert.Equal(["b"], groups.Of("m2").Select(s => s.Id));
        Assert.Empty(groups.Of("m9"));
        Assert.Empty(new SubclipsByMedia([]).Of("m1"));
    }

    /// <summary>
    /// Se cuenta cuántos subclips se leen al armar la lista, no segundos: una sola pasada por la lista de subclips, sea cual
    /// sea el número de medios. Filtrando por cada medio eran medios × subclips (aquí, 2 000 × 4 000 = 8·10⁶).
    /// </summary>
    [Fact]
    public void TheSubclipsAreReadOnce_NotOncePerMedia()
    {
        const int MediaCount = 2_000, SubclipCount = 4_000;
        Media[] media = [.. Enumerable.Range(0, MediaCount).Select(i => new Media($"m{i}"))];
        var subclips = new CountingSubclips(Enumerable.Range(0, SubclipCount).Select(i => Clip($"s{i}", $"m{i % MediaCount}")));

        List<string> items = SubclipsByMedia.Interleave(media, m => m.Id, subclips, m => m.Id, (s, _) => s.Id);

        Assert.Equal(MediaCount + SubclipCount, items.Count);
        Assert.Equal(SubclipCount, subclips.Reads);
    }

    /// <summary>
    /// Lo mismo con <see cref="WorkGrowth"/> (lecturas del id y del medio de los subclips, y bytes reservados): con medios y
    /// subclips creciendo a la vez, filtrar por cada medio era cuadrático.
    /// </summary>
    [Fact]
    public void BuildingTheList_GrowsLinearly() =>
        WorkGrowth.AssertLinear(count => (
                Media: Enumerable.Range(0, count).Select(i => new Media($"m{i}")).ToArray(),
                Subclips: Enumerable.Range(0, 2 * count).Select(i => Clip($"s{i}", $"m{i % count}")).ToArray()),
            input =>
            {
                List<Subclip> items = SubclipsByMedia.Interleave(input.Media, m => m.Id, input.Subclips, _ => (Subclip?)null, (s, _) => s);
                Assert.Empty(items);
                List<string> all = SubclipsByMedia.Interleave(input.Media, m => m.Id, input.Subclips, m => m.Id, (s, _) => s.Id);
                Assert.Equal(3 * input.Media.Length, all.Count);
            }, static () => Subclip.KeyReads, 1_000, 8_000, "Armar la lista de la biblioteca");

    /// <summary>Una lista de subclips que cuenta cuántos se leyeron al recorrerla.</summary>
    private sealed class CountingSubclips(IEnumerable<Subclip> subclips) : IReadOnlyList<Subclip>
    {
        private readonly Subclip[] _subclips = [.. subclips];

        public long Reads { get; private set; }

        public int Count => _subclips.Length;

        public Subclip this[int index]
        {
            get
            {
                Reads++;
                return _subclips[index];
            }
        }

        public IEnumerator<Subclip> GetEnumerator()
        {
            foreach (Subclip subclip in _subclips)
            {
                Reads++;
                yield return subclip;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
