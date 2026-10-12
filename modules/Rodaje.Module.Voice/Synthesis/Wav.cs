namespace Rodaje.Module.Voice.Synthesis;

internal static class Wav
{
    /// <summary>WAV IEEE float mono (para las herramientas de línea de comandos y las pruebas).</summary>
    public static void WriteMonoFloat(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using FileStream fs = File.Create(path);
        using BinaryWriter w = new(fs);
        int data = samples.Length * 4;
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)3);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 4);
        w.Write((short)4);
        w.Write((short)32);
        w.Write("data"u8);
        w.Write(data);
        foreach (float s in samples) w.Write(s);
    }
}
