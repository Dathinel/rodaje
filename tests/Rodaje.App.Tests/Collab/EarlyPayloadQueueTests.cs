using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;

namespace Rodaje.App.Tests.Collab;

/// <summary>La cola de cargas que llegan antes de empezar a editar (revisión, hallazgo 3): acotada en bytes, y al desbordarse se declara perdida.</summary>
public sealed class EarlyPayloadQueueTests
{
    private static Ordenado Ordered(long seq, string text) =>
        new(0, seq, Guid.NewGuid(), "clave", JsonDocument.Parse("{\"d\":\"" + text + "\"}").RootElement.Clone());

    [Fact]
    public void Lo_que_cabe_se_entrega_en_orden_y_vacia_la_cola()
    {
        var queue = new EarlyPayloadQueue(1024);
        queue.Add(Ordered(1, "a"));
        queue.Add(Ordered(2, "b"));

        IReadOnlyList<Ordenado>? replay = queue.Drain();

        Assert.NotNull(replay);
        Assert.Equal([1L, 2L], replay.Select(o => o.Seq).ToArray());
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Lost);
        Assert.Empty(queue.Drain()!);
    }

    [Fact]
    public void Al_pasar_del_tope_de_bytes_la_cola_queda_perdida_y_no_suelta_nada()
    {
        // Antes se descartaban mensajes por encima de un tope de cantidad: la copia recibida quedaba vieja sin que nadie lo supiera.
        var queue = new EarlyPayloadQueue(64);
        queue.Add(Ordered(1, new string('x', 40)));
        queue.Add(Ordered(2, new string('x', 40)));

        Assert.True(queue.Lost);
        Assert.Null(queue.Drain());
    }

    [Fact]
    public void Tras_perderse_no_guarda_nada_hasta_la_proxima_ronda()
    {
        // «Nunca continuar después de descartar»: ni lo que llegue después se cuela en la entrega de esta copia.
        var queue = new EarlyPayloadQueue(64);
        queue.Add(Ordered(1, new string('x', 40)));
        queue.Add(Ordered(2, new string('x', 40)));
        queue.Add(Ordered(3, "b"));

        Assert.Null(queue.Drain());

        // La ronda siguiente (la copia que se pida otra vez) sí empieza a guardar de nuevo.
        queue.Add(Ordered(9, "c"));
        IReadOnlyList<Ordenado>? replay = queue.Drain();
        Assert.NotNull(replay);
        Assert.Equal(9, replay[0].Seq);
    }
}
