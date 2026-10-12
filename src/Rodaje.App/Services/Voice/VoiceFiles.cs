using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rodaje.App.Services.Voice;

/// <summary>
/// Dónde y cómo se guarda la voz generada: un WAV f32 mono en «Voz generada» junto al proyecto (o en
/// <c>%LOCALAPPDATA%\Rodaje\voz</c> si aún no se guardó) y, al lado, un <c>.json</c> con lo que se pidió. El JSON es lo
/// que reconoce un clip como voz generada; el formato del proyecto no cambia.
/// </summary>
public static class VoiceFiles
{
    public const string ProjectFolderName = "Voz generada";

    public static string Folder(string? projectFile) =>
        projectFile is { Length: > 0 } && Path.GetDirectoryName(Path.GetFullPath(projectFile)) is { } dir
            ? Path.Combine(dir, ProjectFolderName)
            : Path.Combine(AppDataPaths.Root, "voz");

    public static string SidecarOf(string wavPath) => Path.ChangeExtension(wavPath, ".json");

    /// <summary>«Voz - primeras palabras - huella.wav»: legible en la biblioteca y distinto para cada texto y voz.</summary>
    public static string FileName(VoiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var words = new StringBuilder();
        foreach (char c in settings.Text.Trim())
        {
            if (words.Length >= 32)
            {
                break;
            }

            words.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        string label = string.Join(' ', words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Clamped().ToSidecar() + DateTime.UtcNow.Ticks));
        string id = Convert.ToHexStringLower(hash.AsSpan(0, 4));
        return (label.Length == 0 ? "Voz" : "Voz - " + label) + " - " + id + ".wav";
    }

    /// <summary>Escribe el WAV y su JSON (cada uno atómico: un apagón no deja un WAV cortado con nombre definitivo).</summary>
    public static string Save(string folder, VoiceSettings settings, SynthesizedVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName(settings));
        WriteWav(path, voice.Samples, voice.SampleRate);
        AppDataPaths.WriteAllTextAtomic(SidecarOf(path), settings.Clamped().ToSidecar());
        return path;
    }

    /// <summary>Los ajustes con que se generó <paramref name="wavPath"/>, o <c>null</c> si no es una voz generada.</summary>
    public static VoiceSettings? ReadSettings(string wavPath)
    {
        string sidecar = SidecarOf(wavPath);
        try
        {
            return File.Exists(sidecar) ? VoiceSettings.FromSidecar(File.ReadAllText(sidecar)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>WAV IEEE float de 32 bits, mono.</summary>
    public static void WriteWav(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        int dataBytes = checked(samples.Length * sizeof(float));
        byte[] header = new byte[44];
        Span<byte> h = header;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 3);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * sizeof(float));
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], sizeof(float));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 32);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], dataBytes);

        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            stream.Write(header);
            stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
