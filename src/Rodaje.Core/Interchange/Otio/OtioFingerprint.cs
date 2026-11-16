using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Model;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>Cálculo de huellas criptográficas SHA-256 para clips y secuencias de OpenTimelineIO.</summary>
internal static class OtioFingerprint
{
    public static string ClipFingerprint(Clip clip, MediaRef? media)
    {
        ArgumentNullException.ThrowIfNull(clip);
        string mediaKey = media?.Path ?? clip.MediaId;
        string speedKey = clip.Speed is null
            ? "none"
            : FormattableString.Invariant($"{clip.Speed.Rate.Num}/{clip.Speed.Rate.Den}:{clip.Speed.Freeze}:{clip.Speed.Reverse}");
        string canonical = FormattableString.Invariant($"{mediaKey}|{clip.SourceIn.Ticks}|{clip.Duration.Ticks}|{speedKey}|{clip.Enabled}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    public static string TimelineFingerprint(TimelineDraft standardDraft)
    {
        ArgumentNullException.ThrowIfNull(standardDraft);
        string canonicalJson = Fidelity.Canonical(standardDraft);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexStringLower(hash);
    }
}
