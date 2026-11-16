using System.Text.Json;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// El resultado de la batería: cada comprobación con pasa/falla, las cifras medidas y la escritura de
/// <c>resultado.json</c> (con Utf8JsonWriter, sin reflexión, para AOT).
/// </summary>
internal sealed class Informe
{
    private readonly List<(string Id, bool Pasa, string Detalle)> _comprobaciones = [];
    private readonly Dictionary<string, double> _medidas = [];

    public string Sistema { get; set; } = "";
    public string Rid { get; set; } = "";
    public int SunPath { get; set; }
    public string RutaBase { get; set; } = "";
    public string RutaEjecucion { get; set; } = "";
    public string RutaSocket { get; set; } = "";

    public int Total => _comprobaciones.Count;
    public int Pasadas { get; private set; }
    public bool TodoPasa { get; private set; } = true;

    public void Comprueba(string id, bool pasa, string detalle)
    {
        _comprobaciones.Add((id, pasa, detalle));
        if (pasa) Pasadas++; else TodoPasa = false;
        Console.WriteLine($"  {(pasa ? "pasa " : "FALLA")}  {id,-28} {detalle}");
    }

    public void Medida(string id, double valor) => _medidas[id] = valor;

    public void EscribeJson(string path)
    {
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("sistema", Sistema);
        w.WriteString("rid", Rid);
        w.WriteString("fecha", DateTimeOffset.UtcNow.ToString("o"));
        w.WriteNumber("sun_path", SunPath);
        w.WritePropertyName("rutas");
        w.WriteStartObject();
        w.WriteString("base", RutaBase);
        w.WriteString("ejecucion", RutaEjecucion);
        w.WriteString("socket", RutaSocket);
        w.WriteNumber("socket_bytes", System.Text.Encoding.UTF8.GetByteCount(RutaSocket));
        w.WriteEndObject();
        w.WritePropertyName("comprobaciones");
        w.WriteStartArray();
        foreach ((string id, bool pasa, string detalle) in _comprobaciones)
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteBoolean("pasa", pasa);
            w.WriteString("detalle", detalle);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("medidas");
        w.WriteStartObject();
        foreach (KeyValuePair<string, double> m in _medidas)
            w.WriteNumber(m.Key, m.Value);
        w.WriteEndObject();
        w.WriteBoolean("pasa", TodoPasa);
        w.WriteEndObject();
    }
}
