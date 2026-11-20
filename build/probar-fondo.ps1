# Comprueba que una corrida automática de Rodaje (--arranque, --medir, --autoprueba) no pone ninguna ventana a la vista:
# lanza el exe y, hasta que termina, mira cada 15 ms las ventanas de los procesos Rodaje nacidos desde el lanzamiento.
# Falla si alguna es visible y toca el escritorio (cualquier monitor) o si alguna pasa a primer plano.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\probar-fondo.ps1 [-Exe dist\Rodaje\app\Rodaje.exe]
#       [-Argumentos '--arranque --sin-audio'] [-Segundos 120]
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$Argumentos = '--arranque --sin-audio',
    [int]$Segundos = 120
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$Raiz = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'comun.psm1') -DisableNameChecking -Force
if (-not $Exe) { $Exe = Join-Path $Raiz 'dist\Rodaje\app\Rodaje.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if ($Argumentos -match '--visible') { Mal 'Con --visible la ventana se ve a propósito: no hay nada que comprobar.'; exit 1 }
$real = Join-Path (Split-Path -Parent $Exe) 'app\Rodaje.exe'
if (-not (Test-Path -LiteralPath $real)) { $real = $Exe }
Exigir-Fondo $real

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class VigiaFondo
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string f, uint acceso, uint compartir, IntPtr s, uint modo, uint marcas, IntPtr t);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr h, System.Text.StringBuilder b, uint n, uint marcas);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    // Ruta final (sin uniones ni enlaces): el lanzador abre app\Rodaje.exe por la ruta real y no por la que se le pasó.
    private static string Final(string ruta)
    {
        IntPtr h = CreateFileW(ruta, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
        if (h == new IntPtr(-1)) { return ruta; }
        var b = new System.Text.StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, b, 1024, 0);
        CloseHandle(h);
        return n > 0 && n < 1024 ? b.ToString() : ruta;
    }

    // Solo los procesos de este exe (o del app\Rodaje.exe que abre el lanzador): puede haber otras corridas a la vez.
    private static bool Mio(Process p, string exe, string real)
    {
        string ruta = Final(p.MainModule.FileName);
        return string.Equals(ruta, exe, StringComparison.OrdinalIgnoreCase) || string.Equals(ruta, real, StringComparison.OrdinalIgnoreCase);
    }

    // Devuelve [ventanas vistas, a la vista, en primer plano, código de salida] y describe las que asomaron.
    public static int[] Vigilar(string exe, string real, string argumentos, int limiteMs, List<string> asomaron)
    {
        DateTime inicio = DateTime.Now.AddSeconds(-1);
        string exeFinal = Final(exe), realFinal = Final(real);
        var lanzado = Process.Start(new ProcessStartInfo(exe, argumentos) { UseShellExecute = false });
        var reloj = Stopwatch.StartNew();
        var vistas = new HashSet<IntPtr>();
        var malas = new HashSet<IntPtr>();
        int primerPlano = 0;
        // Escritorio virtual: SM_XVIRTUALSCREEN 76, SM_YVIRTUALSCREEN 77, SM_CXVIRTUALSCREEN 78, SM_CYVIRTUALSCREEN 79.
        int x0 = GetSystemMetrics(76), y0 = GetSystemMetrics(77), x1 = x0 + GetSystemMetrics(78), y1 = y0 + GetSystemMetrics(79);
        while (reloj.ElapsedMilliseconds < limiteMs)
        {
            var pids = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName("Rodaje"))
            {
                try { if (p.StartTime >= inicio && Mio(p, exeFinal, realFinal)) { pids.Add((uint)p.Id); } } catch (Exception) { } finally { p.Dispose(); }
            }

            if (pids.Count == 0 && lanzado.HasExited) { break; }
            IntPtr frente = GetForegroundWindow();
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) { return true; }
                vistas.Add(h);
                RECT r;
                if (h == frente && malas.Add(h)) { primerPlano++; asomaron.Add("primer plano: 0x" + h.ToInt64().ToString("X")); }
                if (IsWindowVisible(h) && GetWindowRect(h, out r) && r.R > r.L && r.B > r.T && r.L < x1 && r.R > x0 && r.T < y1 && r.B > y0 && malas.Add(h))
                {
                    asomaron.Add(string.Format("a la vista: 0x{0:X} en ({1}, {2})-({3}, {4}) a los {5} ms", h.ToInt64(), r.L, r.T, r.R, r.B, reloj.ElapsedMilliseconds));
                }

                return true;
            }, IntPtr.Zero);
            System.Threading.Thread.Sleep(15);
        }

        bool termino = lanzado.HasExited;
        if (!termino) { foreach (Process p in Process.GetProcessesByName("Rodaje")) { try { if (p.StartTime >= inicio && Mio(p, exeFinal, realFinal)) { p.Kill(); } } catch (Exception) { } } }
        return new[] { vistas.Count, malas.Count - primerPlano, primerPlano, termino ? lanzado.ExitCode : -999 };
    }
}
'@

Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
Titulo "Vigilando las ventanas de: $(Split-Path -Leaf $Exe) $Argumentos"
$asomaron = New-Object 'System.Collections.Generic.List[string]'
$r = [VigiaFondo]::Vigilar($Exe, $real, $Argumentos, $Segundos * 1000, $asomaron)
Nota "Ventanas del proceso vistas: $($r[0]); a la vista: $($r[1]); en primer plano: $($r[2]); código de salida: $($r[3])"
foreach ($a in $asomaron) { Mal $a }
if ($r[0] -eq 0) { Mal 'No se vio ninguna ventana del proceso: la comprobación no vale.'; exit 1 }
if ($r[3] -ne 0) { Mal "Rodaje terminó con código $($r[3])."; exit 1 }
if ($asomaron.Count -gt 0) { exit 1 }
Bien 'Ninguna ventana a la vista ni en primer plano.'
exit 0
