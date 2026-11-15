using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>
/// <see cref="TrimFixture"/> con una transcripción de m2 (200 cuadros): la palabra <c>w{i}</c> ocupa los cuadros
/// <c>[10i, 10i + 6)</c>, así que entre dos palabras hay un hueco de 4 cuadros.
/// </summary>
internal static class TranscriptFixture
{
    public const int WordCount = 20;

    public static TrimFixture WithTranscript(this TrimFixture f, string mediaId = "m2", int words = WordCount, int step = 10, int length = 6)
    {
        f.Project.Transcripts.Add(new MediaTranscript
        {
            Id = "t-" + mediaId,
            MediaId = mediaId,
            Language = "es",
            Model = "prueba",
            MediaDuration = f.Project.GetMedia(mediaId).Duration,
            Words = [.. Enumerable.Range(0, words).Select(i => new SpokenWord
            {
                Start = F(i * step),
                End = F(i * step + length),
                Text = "w" + i,
                Confidence = 0.9,
            })],
        });
        return f;
    }

    public static TranscriptDocument Doc(this TrimFixture f, string? track = null) => TranscriptDocument.Build(f.Project, f.Sequence, track);

    public static string[] Texts(this TranscriptDocument doc) => [.. doc.Words.Select(w => w.Text)];

    /// <summary>Da al clip una velocidad y le ajusta el mapa de tiempo, como la pestaña Velocidad.</summary>
    public static Clip Retime(this TrimFixture f, string clipId, ClipSpeed speed)
    {
        Clip clip = f.Clip(clipId);
        clip.Speed = speed;
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, speed).ApplyTo(clip);
        return clip;
    }
}
