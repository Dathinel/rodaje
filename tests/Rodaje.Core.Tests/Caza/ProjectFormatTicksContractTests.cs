using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Unidades: el tiempo en ticks» de <c>docs/formato-proyecto.md</c>, comprobado al leer y escribir un
/// <c>.rodaje</c> entero. Cada prueba cita la frase.
/// </summary>
public sealed class ProjectFormatTicksContractTests
{
    private static JsonObject RichRoot() =>
        JsonNode.Parse(ProjectSerializer.Serialize(SerializationSamples.Rich()))!.AsObject();

    private static JsonObject FirstClip(JsonObject root) =>
        root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

    // «Un segundo tiene exactamente 705 600 000 ticks.»
    [Fact]
    public void Un_segundo_son_705_600_000_ticks()
    {
        Assert.Equal(705_600_000, Time.TicksPerSecond);
        Assert.Equal(705_600_000, Time.FromSeconds(1).Ticks);
    }

    // «es divisible entre la duración de un cuadro a 24, 25, 30, 48, 50, 60, 90, 100 y 120 cuadros por segundo, también
    // en las variantes NTSC que multiplican la tasa por 1000/1001 (23,976, 29,97 y 59,94 fps)» … «el inicio de cada
    // cuadro … cae siempre en un tick entero»
    [Theory]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30, 1)]
    [InlineData(48, 1)]
    [InlineData(50, 1)]
    [InlineData(60, 1)]
    [InlineData(90, 1)]
    [InlineData(100, 1)]
    [InlineData(120, 1)]
    [InlineData(24000, 1001)]
    [InlineData(30000, 1001)]
    [InlineData(60000, 1001)]
    public void Cada_cuadro_empieza_en_un_tick_entero(long num, long den)
    {
        var rate = new Rational(num, den);
        Assert.Equal(0, Time.TicksPerSecond * rate.Den % rate.Num);
        long frameTicks = Time.TicksPerSecond * rate.Den / rate.Num;
        for (long frame = 0; frame < 1000; frame++)
        {
            Time start = Time.FromFrames(frame, rate);
            Assert.Equal(frame * frameTicks, start.Ticks);
            Assert.Equal(frame, start.ToFrames(rate));
        }
    }

    // «y entre la duración de una muestra de audio a 8, 16, 22,05, 24, 32, 44,1, 48, 88,2, 96 y 192 kHz»
    [Theory]
    [InlineData(8_000)]
    [InlineData(16_000)]
    [InlineData(22_050)]
    [InlineData(24_000)]
    [InlineData(32_000)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(88_200)]
    [InlineData(96_000)]
    [InlineData(192_000)]
    public void Cada_muestra_empieza_en_un_tick_entero(int sampleRate)
    {
        Assert.Equal(0, Time.TicksPerSecond % sampleRate);
        long sampleTicks = Time.TicksPerSecond / sampleRate;
        for (long sample = 0; sample < 1000; sample++)
        {
            Time start = Time.FromSamples(sample, sampleRate);
            Assert.Equal(sample * sampleTicks, start.Ticks);
            Assert.Equal(sample, start.ToSamples(sampleRate));
        }
    }

    // «sumar o restar posiciones no acumula el error de redondeo»
    [Fact]
    public void Sumar_cuadros_no_acumula_error()
    {
        Time frame = Time.FromFrames(1, Rational.Ntsc30);
        Time sum = Time.Zero;
        for (int i = 0; i < 100_000; i++)
        {
            sum += frame;
        }

        Assert.Equal(Time.FromFrames(100_000, Rational.Ntsc30), sum);
        Assert.Equal(100_000, sum.ToFrames(Rational.Ntsc30));
    }

    // «a 29,97 fps un cuadro dura 705 600 000 × 1001 / 30 000 = 23 543 520 ticks exactos»
    [Fact]
    public void Un_cuadro_a_29_97_son_23_543_520_ticks()
    {
        Assert.Equal(23_543_520, Time.FromFrames(1, Rational.Ntsc30).Ticks);
        Assert.Equal(23_543_520, 705_600_000L * 1001 / 30_000);
    }

    // «Para convertir, un programa externo divide entre 705 600 000 y obtiene segundos.» Y lo que Rodaje escribe es eso.
    [Fact]
    public void Rodaje_escribe_los_tiempos_como_ticks_enteros()
    {
        Project project = SerializationSamples.Rich();
        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        clip.Start = Time.FromSeconds(2.5);

        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        JsonValue start = FirstClip(root)["start"]!.AsValue();
        Assert.Equal(1_764_000_000L, start.GetValue<long>());
        Assert.Equal(2.5, start.GetValue<long>() / 705_600_000.0);
    }

    // «un tiempo con decimales o escrito como texto hace que el archivo se rechace»
    [Theory]
    [InlineData("start", "1.5")]
    [InlineData("start", "1411200000.0")]
    [InlineData("start", "1.4112e9")]
    [InlineData("start", "\"1411200000\"")]
    [InlineData("sourceIn", "0.5")]
    [InlineData("sourceOut", "\"4237833600\"")]
    public void Un_tiempo_con_decimales_o_como_texto_rechaza_el_archivo(string field, string value)
    {
        JsonObject root = RichRoot();
        FirstClip(root)[field] = JsonNode.Parse(value);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    // Lo mismo en una duración: «Todos los tiempos del archivo, sean posiciones o duraciones».
    [Theory]
    [InlineData("7063056000.5")]
    [InlineData("\"7063056000\"")]
    public void Una_duracion_con_decimales_o_como_texto_rechaza_el_archivo(string value)
    {
        JsonObject root = RichRoot();
        root["media"]![0]!["duration"] = JsonNode.Parse(value);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    // «Los valores caben en un entero de 64 bits con signo.» Uno que no cabe se rechaza en vez de envolverse.
    [Fact]
    public void Un_tiempo_que_no_cabe_en_64_bits_rechaza_el_archivo()
    {
        JsonObject root = RichRoot();
        FirstClip(root)["start"] = JsonNode.Parse("9223372036854775808");
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    // «ese límite [2^53] equivale a unos 148 días de material»
    [Fact]
    public void Dos_a_la_53_ticks_son_unos_148_dias()
    {
        double days = (1L << 53) / (double)Time.TicksPerSecond / 86_400;
        Assert.Equal(148, Math.Round(days));
    }

    // «Las tasas de cuadros no son decimales sino fracciones exactas con la forma {"num": 30000, "den": 1001}.»
    [Fact]
    public void Rodaje_escribe_la_tasa_como_fraccion()
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].FrameRate = Rational.Ntsc30;

        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        JsonObject rate = root["sequences"]![0]!["frameRate"]!.AsObject();
        Assert.Equal(2, rate.Count);
        Assert.Equal(30000, rate["num"]!.GetValue<long>());
        Assert.Equal(1001, rate["den"]!.GetValue<long>());
    }

    // «Los dos campos son obligatorios, enteros y mayores que cero.»
    [Theory]
    [InlineData("29.97")]
    [InlineData("\"30000/1001\"")]
    [InlineData("""{"num": 30000}""")]
    [InlineData("""{"den": 1001}""")]
    [InlineData("""{"num": 30000.5, "den": 1001}""")]
    [InlineData("""{"num": 30, "den": "1"}""")]
    [InlineData("""{"num": 0, "den": 1}""")]
    [InlineData("""{"num": 30, "den": 0}""")]
    [InlineData("""{"num": -30, "den": -1}""")]
    public void Una_tasa_que_no_es_fraccion_positiva_rechaza_el_archivo(string value)
    {
        JsonObject root = RichRoot();
        root["sequences"]![0]!["frameRate"] = JsonNode.Parse(value);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
    }

    // «Al leer, la fracción se simplifica, de modo que {"num": 60, "den": 2} se entiende como 30/1, y Rodaje siempre la
    // escribe ya simplificada.»
    [Fact]
    public void La_tasa_se_simplifica_al_leer_y_se_escribe_simplificada()
    {
        JsonObject root = RichRoot();
        root["sequences"]![0]!["frameRate"] = JsonNode.Parse("""{"num": 60, "den": 2}""");
        root["media"]![0]!["frameRate"] = JsonNode.Parse("""{"num": 60000, "den": 2002}""");

        Project project = ProjectSerializer.Deserialize(root.ToJsonString());
        Assert.Equal(new Rational(30, 1), project.Sequences[0].FrameRate);
        Assert.Equal((30L, 1L), (project.Sequences[0].FrameRate.Num, project.Sequences[0].FrameRate.Den));
        Assert.Equal((30000L, 1001L), (project.Media[0].FrameRate!.Value.Num, project.Media[0].FrameRate!.Value.Den));

        JsonObject written = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        Assert.Equal(30, written["sequences"]![0]!["frameRate"]!["num"]!.GetValue<long>());
        Assert.Equal(1, written["sequences"]![0]!["frameRate"]!["den"]!.GetValue<long>());
        Assert.Equal(30000, written["media"]![0]!["frameRate"]!["num"]!.GetValue<long>());
        Assert.Equal(1001, written["media"]![0]!["frameRate"]!["den"]!.GetValue<long>());
    }
}
