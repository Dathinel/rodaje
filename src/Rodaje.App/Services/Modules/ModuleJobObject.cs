using System.Diagnostics;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// Job Object con «matar al cerrar» para los procesos de módulos: si Rodaje termina (aunque sea de golpe), Windows cierra
/// el handle y mata todo lo que quedó dentro. <see cref="Dispose"/> hace lo mismo a propósito.
/// </summary>
public sealed unsafe partial class ModuleJobObject : IDisposable
{
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE de winnt.h (TerraFX no la declara).
    private const uint KillOnJobClose = 0x2000;

    private nint _handle;

    public ModuleJobObject()
    {
        HANDLE job = CreateJobObjectW(null, null);
        if (job == HANDLE.NULL)
        {
            return;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
        limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limits,
            (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0)
        {
            CloseHandle(job);
            return;
        }

        _handle = (nint)job.Value;
    }

    /// <summary>Windows dio el Job Object (si no, los módulos funcionan igual pero sin esa garantía).</summary>
    public bool IsAvailable => _handle != 0;

    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        nint job = _handle;
        try
        {
            return job != 0 && AssignProcessToJobObject((HANDLE)job, (HANDLE)process.Handle) != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        nint job = Interlocked.Exchange(ref _handle, 0);
        if (job != 0)
        {
            CloseHandle((HANDLE)job);
        }
    }
}
