using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// Regresión de la matriz: un HDR10 (PQ) o HLG entraba sin aviso y se veía y exportaba como SDR, con los colores
/// apagados. La importación informa ahora de la conversión y del control de brillo.
/// </summary>
public sealed class HdrImportTests
{
    [HdrFact]
    public void Importing_hdr_informs_that_it_is_converted_to_sdr() => UiThread.Run(async () =>
    {
        string pq = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mp4-hdr10-pq")).Path!;
        string hlg = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mov-hlg")).Path!;
        string sdr = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mp4-hevc10-aac")).Path!;
        var session = new EditorSession(new FakePlayback(), new FfmpegMediaProber(), new FakeDialogs(), new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        await session.ImportMediaAsync([sdr]);
        Assert.Empty(notices);

        await session.ImportMediaAsync([pq, hlg]);
        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Informational, notice.Severity);
        Assert.Contains("HDR: se convierte a SDR; ajusta el brillo en Propiedades", notice.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(pq) + " (PQ)", notice.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(hlg) + " (HLG)", notice.Message, StringComparison.Ordinal);
        Assert.Equal(3, session.Project.Media.Count);
    });
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class HdrFactAttribute : FactAttribute
{
    public HdrFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.FfmpegLibraries,
        FfmpegTool.Default is { } ffmpeg ? ffmpeg.ProbeEncoder("hevc_nvenc") : FfmpegTool.UnavailableReason);
}
