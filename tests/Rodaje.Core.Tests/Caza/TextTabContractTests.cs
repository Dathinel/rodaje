using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «La pestaña Texto» de <c>docs/guia/titulos-y-subtitulos.md</c> y se cumple en <c>Rodaje.Core.Text</c>.
/// Cada prueba cita la frase.
/// </summary>
public sealed class TextTabContractTests
{
    private static readonly Func<TextSpan, TextSpan> Bold = s => s with { Weight = 700 };

    // «elige una o varias palabras (o deja el cursor en una) y dales **negrita**, *cursiva*, subrayado, otro color o más o
    // menos tamaño»
    [Fact]
    public void Format_applies_to_the_chosen_words_only()
    {
        const string text = "hola mundo feliz";
        List<TextSpan>? spans = TextSpans.Apply(null, text.Length, 5, 11, Bold);
        spans = TextSpans.Apply(spans, text.Length, 5, 5, s => s with { Italic = true, Underline = true, Color = "#FF0000", Scale = 1.5 });

        Assert.NotNull(spans);
        Assert.Equal(2, spans.Count);
        Assert.Equal(new TextSpan { Start = 5, Length = 5, Weight = 700, Italic = true, Underline = true, Color = "#FF0000", Scale = 1.5 }, spans[0]);
        Assert.Equal(new TextSpan { Start = 10, Length = 6, Weight = 700 }, spans[1]);
        Assert.Null(TextSpans.At(spans, 0));
    }

    // «más o menos tamaño»
    [Fact]
    public void Size_change_is_kept_within_its_limits()
    {
        List<TextSpan>? bigger = TextSpans.Apply(null, 4, 0, 4, s => s with { Scale = 1000 });
        List<TextSpan>? smaller = TextSpans.Apply(null, 4, 0, 4, s => s with { Scale = 0.0001 });

        Assert.Equal(TextSpans.MaxScale, Assert.Single(bigger!).Scale);
        Assert.Equal(TextSpans.MinScale, Assert.Single(smaller!).Scale);
    }

    // «✕ quita ese formato»
    [Fact]
    public void Clearing_a_format_removes_it_and_keeps_the_rest()
    {
        const string text = "hola mundo";
        List<TextSpan>? spans = TextSpans.Apply(null, text.Length, 0, 10, s => s with { Weight = 700, Italic = true });

        List<TextSpan>? noBold = TextSpans.Apply(spans, text.Length, 0, 10, s => s with { Weight = null });
        Assert.Equal(new TextSpan { Start = 0, Length = 10, Italic = true }, Assert.Single(noBold!));

        Assert.Null(TextSpans.Apply(noBold, text.Length, 0, 10, s => s with { Italic = null }));
    }

    // «El formato sigue a su palabra mientras escribes.»
    [Fact]
    public void Format_follows_its_word_while_typing()
    {
        // «mundo» en negrita.
        List<TextSpan>? spans = TextSpans.Apply(null, 10, 5, 5, Bold);

        // Escribir antes la corre sin agrandarla.
        List<TextSpan>? before = TextSpans.Remap("hola mundo", "hola, mundo", spans);
        Assert.Equal(new TextSpan { Start = 6, Length = 5, Weight = 700 }, Assert.Single(before!));

        // Escribir al final de la palabra la continúa.
        List<TextSpan>? after = TextSpans.Remap("hola mundo", "hola mundos", spans);
        Assert.Equal(new TextSpan { Start = 5, Length = 6, Weight = 700 }, Assert.Single(after!));

        // Escribir dentro de la palabra queda en ella.
        List<TextSpan>? inside = TextSpans.Remap("hola mundo", "hola munndo", spans);
        Assert.Equal(new TextSpan { Start = 5, Length = 6, Weight = 700 }, Assert.Single(inside!));

        // Escribir justo antes de su comienzo no la continúa.
        List<TextSpan>? atStart = TextSpans.Remap("hola mundo", "hola Xmundo", spans);
        Assert.Equal(new TextSpan { Start = 6, Length = 5, Weight = 700 }, Assert.Single(atStart!));

        // Borrar la palabra se lleva su formato.
        Assert.Null(TextSpans.Remap("hola mundo", "hola ", spans));
    }

    // «mayúsculas (como se escribe, MAYÚSCULAS, minúsculas o versalitas)»
    [Fact]
    public void Casing_offers_the_four_modes_without_changing_length()
    {
        const string text = "Ñandú Año";
        Assert.Equal(text, TextCasing.Apply(text, TextCase.None));
        Assert.Equal("ÑANDÚ AÑO", TextCasing.Apply(text, TextCase.Upper));
        Assert.Equal("ñandú año", TextCasing.Apply(text, TextCase.Lower));

        Span<bool> lowered = stackalloc bool[text.Length];
        Assert.Equal("ÑANDÚ AÑO", TextCasing.Apply(text, TextCase.SmallCaps, lowered));
        Assert.Equal([false, true, true, true, true, false, false, true, true], lowered.ToArray());
    }

    // «miniaturas animadas de Desvanecer, Subir, Bajar, Rebote, Escala, Elástico, Desenfoque, Girar, Máquina de escribir y
    // Líneas»
    [Fact]
    public void Animation_gallery_has_the_ten_named_presets_in_order()
    {
        Assert.Equal(
            ["Desvanecer", "Subir", "Bajar", "Rebote", "Escala", "Elástico", "Desenfoque", "Girar", "Máquina de escribir", "Líneas"],
            TextAnimationPresets.All.Select(p => p.Name));
    }

    // «dos galerías, entrada y salida»
    [Fact]
    public void Each_preset_has_an_entry_and_an_exit()
    {
        foreach (TextAnimationPreset preset in TextAnimationPresets.All)
        {
            Assert.Same(preset, TextAnimationPresets.Match(preset.In(), entry: true));
            Assert.Same(preset, TextAnimationPresets.Match(preset.Out(), entry: false));
        }
    }

    // «**Por** (bloque, línea, palabra o letra)»
    [Fact]
    public void By_offers_block_line_word_and_letter()
    {
        Assert.Equal([TextUnit.Block, TextUnit.Line, TextUnit.Word, TextUnit.Letter], Enum.GetValues<TextUnit>());
        TextAnimationPreset fade = TextAnimationPresets.Find("desvanecer")!;
        foreach (TextUnit unit in Enum.GetValues<TextUnit>())
        {
            Assert.Equal(unit, fade.In(unit).Unit);
            Assert.Equal(unit != TextUnit.Block, TextAnimator.From(fade.In(unit)) is not null);
        }
    }

    // «**Desfase** (0 %: todas a la vez»
    [Fact]
    public void Zero_stagger_animates_all_units_at_once()
    {
        TextAnimator animator = TextAnimator.From(TextAnimationPresets.Find("desvanecer")!.In(TextUnit.Letter, stagger: 0))!;
        Span<double> delays = stackalloc double[5];
        TextAnimator.Delays(UnitOrder.Forward, delays);

        foreach (double progress in new[] { 0, 0.25, 0.5, 1 })
        {
            double first = animator.Evaluate(progress, delays[0]).Opacity;
            foreach (double delay in delays)
            {
                Assert.Equal(first, animator.Evaluate(progress, delay).Opacity);
            }
        }
    }

    // «100 %: una tras otra, de golpe)»
    [Fact]
    public void Full_stagger_pops_units_one_after_another()
    {
        TextAnimator animator = TextAnimator.From(TextAnimationPresets.Find("desvanecer")!.In(TextUnit.Letter, stagger: 1))!;
        Span<double> delays = stackalloc double[5];
        TextAnimator.Delays(UnitOrder.Forward, delays);

        for (int step = 0; step <= 40; step++)
        {
            double progress = step / 40.0;
            int visible = 0;
            for (int k = 0; k < delays.Length; k++)
            {
                double opacity = animator.Evaluate(progress, delays[k]).Opacity;
                Assert.True(opacity is 0 or 1, $"opacidad {opacity} en {progress}: no es de golpe");
                if (opacity == 1)
                {
                    Assert.Equal(visible, k);
                    visible++;
                }
            }
        }

        Assert.Equal(0, animator.Evaluate(0.1, delays[^1]).Opacity);
        Assert.Equal(1, animator.Evaluate(1, delays[^1]).Opacity);
    }

    // «**Orden** (de la primera a la última, al revés, desde el centro, desde los extremos o mezclado)»
    [Theory]
    [InlineData(UnitOrder.Forward, new[] { 0, 1, 2, 3, 4 })]
    [InlineData(UnitOrder.Backward, new[] { 4, 3, 2, 1, 0 })]
    [InlineData(UnitOrder.Center, new[] { 2, 1, 0, 1, 2 })]
    [InlineData(UnitOrder.Edges, new[] { 0, 1, 2, 1, 0 })]
    public void Order_sets_which_unit_starts_first(UnitOrder order, int[] rank)
    {
        Span<double> delays = stackalloc double[5];
        TextAnimator.Delays(order, delays);
        for (int a = 0; a < rank.Length; a++)
        {
            for (int b = 0; b < rank.Length; b++)
            {
                Assert.Equal(Math.Sign(rank[a] - rank[b]), Math.Sign(delays[a] - delays[b]));
            }
        }
    }

    // «o mezclado»
    [Fact]
    public void Mixed_order_is_a_fixed_shuffle_of_every_unit()
    {
        double[] delays = new double[8];
        TextAnimator.Delays(UnitOrder.Random, delays);
        double[] again = new double[8];
        TextAnimator.Delays(UnitOrder.Random, again);

        Assert.Equal(delays, again);
        Assert.Equal(Enumerable.Range(0, 8).Select(k => k / 7.0), delays.Order());
        Assert.NotEqual(Enumerable.Range(0, 8).Select(k => k / 7.0), delays);
    }

    // «**Duración**»
    [Fact]
    public void Duration_sets_how_long_the_animation_lasts()
    {
        StyleAnimation animation = TextAnimationPresets.Find("subir")!.In(seconds: 2.5);
        Assert.Equal(2.5, animation.DurationSeconds);
        Assert.Equal(2.5, TextAnimator.From(animation)!.Duration);
    }

    // «**Convertir en keyframes**, que pasa a Propiedades las animaciones del bloque entero (las de línea, palabra o letra
    // quedan en el estilo)»
    [Fact]
    public void Only_whole_block_animations_are_not_per_unit()
    {
        Assert.False(TextAnimationPresets.Find("subir")!.In(TextUnit.Block).PerUnit);
        Assert.True(TextAnimationPresets.Find("subir")!.In(TextUnit.Line).PerUnit);
        Assert.True(TextAnimationPresets.Find("subir")!.In(TextUnit.Word).PerUnit);
        Assert.True(TextAnimationPresets.Find("subir")!.In(TextUnit.Letter).PerUnit);
    }
}
