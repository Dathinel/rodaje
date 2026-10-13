using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Rodaje.Media.Probe;

/// <summary>Lo que el archivo dice de su grabación: cuándo y con qué cámara. Nulo lo que no trae.</summary>
public sealed record MediaTags(DateTimeOffset? Created, string? Camera)
{
    public static MediaTags None { get; } = new(null, null);

    /// <summary>
    /// Lee las etiquetas habituales de QuickTime/MP4 (<c>creation_time</c>, <c>com.apple.quicktime.creationdate</c>,
    /// <c>make</c>/<c>model</c> y sus variantes de Apple y Android). Las claves no distinguen mayúsculas.
    /// </summary>
    public static MediaTags Parse(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        string? Get(params string[] keys)
        {
            foreach (string key in keys)
            {
                foreach ((string k, string v) in tags)
                {
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
                    {
                        return v.Trim();
                    }
                }
            }

            return null;
        }

        DateTimeOffset? created = null;
        if (Get("com.apple.quicktime.creationdate", "creation_time", "date") is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            && parsed.Year > 1970)
        {
            created = parsed;
        }

        string? make = Get("com.apple.quicktime.make", "com.android.manufacturer", "make");
        string? model = Get("com.apple.quicktime.model", "com.android.model", "model");
        string? camera = (make, model) switch
        {
            (null, null) => null,
            ({ } m, null) => m,
            (null, { } o) => o,
            ({ } m, { } o) when o.StartsWith(m, StringComparison.OrdinalIgnoreCase) => o,
            ({ } m, { } o) => $"{m} {o}",
        };
        return new MediaTags(created, camera);
    }

    internal static unsafe Dictionary<string, string> Read(AVDictionary* dictionary, Dictionary<string, string>? into = null)
    {
        var result = into ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dictionary, "", entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            string? key = Marshal.PtrToStringUTF8((nint)entry->key);
            string? value = Marshal.PtrToStringUTF8((nint)entry->value);
            if (key is not null && value is not null)
            {
                result.TryAdd(key, value);
            }
        }

        return result;
    }
}
