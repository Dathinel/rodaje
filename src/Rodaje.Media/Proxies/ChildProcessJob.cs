using System.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Media.Proxies;

/// <summary>
/// Objeto de trabajo de Windows con <c>KILL_ON_JOB_CLOSE</c>: si Rodaje termina de golpe, Windows cierra el
/// trabajo y mata los <c>ffmpeg.exe</c> que quedaron dentro, en vez de dejarlos escribiendo proxies huérfanos.
/// </summary>
public static unsafe class ChildProcessJob
{
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE de winnt.h (TerraFX no la declara).
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Job = new(Create);

    /// <summary>Mete el proceso en el trabajo; si Windows no lo permite, el proceso sigue igual sin esa garantía.</summary>
    public static bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        nint job = Job.Value;
        return job != 0 && AssignProcessToJobObject((HANDLE)job, (HANDLE)process.Handle) != 0;
    }

    private static nint Create()
    {
        HANDLE job = CreateJobObjectW(null, null);
        if (job == HANDLE.NULL)
        {
            return 0;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits,
            (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0)
        {
            CloseHandle(job);
            return 0;
        }

        // El handle vive lo que vive el proceso: al cerrarse Rodaje, Windows lo cierra y mata a los hijos.
        return (nint)job.Value;
    }
}
