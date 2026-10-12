# Mide el arranque de un exe de ventana desde fuera: hasta que existe su ventana, hasta que es visible y hasta que su
# cola de mensajes queda libre (WaitForInputIdle); si el exe acepta «--registro ruta» y escribe ahí líneas
# «arranque.<marca>_ms: N» antes de cerrarse solo, también reúne esas marcas (Rodaje lo hace con --arranque).
# En frío: antes de cada vuelta copia la carpeta del exe con E/S sin búfer (robocopy /J), así sus archivos no están en la
# caché de Windows; las DLL del sistema sí lo están, como tras un reinicio con el escritorio ya cargado.
# Uso:
#   powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\medir-arranque.ps1 [-Exe dist\Rodaje\app\Rodaje.exe]
#       [-Veces 10] [-Frio] [-Argumentos '--arranque'] [-Etiqueta rodaje] [-Matar ms]
#   -Matar N   para un exe que no se cierra solo: lo termina N ms después de que su cola quede libre.
#   -Exe dist\Rodaje\Rodaje.exe mide a través del lanzador (la ventana es del app\Rodaje.exe que este abre).
[CmdletBinding()]
param(
    [string]$Exe = '',
    [int]$Veces = 10,
    [switch]$Frio,
    [string]$Argumentos = '--arranque',
    [string]$Etiqueta = '',
    [int]$Matar = 0
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$Raiz = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $Raiz 'dist\Rodaje\app\Rodaje.exe' }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if (-not $Etiqueta) { $Etiqueta = [IO.Path]::GetFileNameWithoutExtension($Exe) }

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class SondaArranque
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint acceso, bool heredar, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint f, System.Text.StringBuilder s, ref uint n);

    static bool EsDelMismoNombre(uint pid, string nombre, System.Collections.Generic.Dictionary<uint, bool> cache)
    {
        bool r;
        if (cache.TryGetValue(pid, out r)) return r;
        r = false;
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(1024); uint n = 1024;
            if (QueryFullProcessImageNameW(h, 0, sb, ref n)) r = string.Equals(System.IO.Path.GetFileNameWithoutExtension(sb.ToString()), nombre, StringComparison.OrdinalIgnoreCase);
            CloseHandle(h);
        }
        cache[pid] = r;
        return r;
    }

    // [ventana creada, ventana visible, cola libre, código de salida]. La ventana puede ser de otro proceso con el mismo
    // nombre que nazca después (un lanzador que abre el exe de verdad y sale).
    public static double[] Medir(string exe, string args, int limiteMs, int matar)
    {
        string nombre = System.IO.Path.GetFileNameWithoutExtension(exe);
        var previos = new System.Collections.Generic.HashSet<uint>();
        foreach (var x in Process.GetProcessesByName(nombre)) { previos.Add((uint)x.Id); x.Dispose(); }
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) };
        var reloj = Stopwatch.StartNew();
        Process lanzado = Process.Start(psi);
        uint pid = (uint)lanzado.Id;
        uint dueñoVentana = 0;
        double creada = -1, visible = -1, libre = -1;
        var cache = new System.Collections.Generic.Dictionary<uint, bool>();
        while (visible < 0 && reloj.ElapsedMilliseconds < limiteMs && !(lanzado.HasExited && lanzado.ExitCode != 0))
        {
            EnumWindows((h, l) =>
            {
                uint dueño; GetWindowThreadProcessId(h, out dueño);
                if (GetWindow(h, 4) != IntPtr.Zero || previos.Contains(dueño)) return true;
                if (dueño != pid && !EsDelMismoNombre(dueño, nombre, cache)) return true;
                if (creada < 0) creada = reloj.Elapsed.TotalMilliseconds;
                if (IsWindowVisible(h)) { visible = reloj.Elapsed.TotalMilliseconds; dueñoVentana = dueño; return false; }
                return true;
            }, IntPtr.Zero);
            System.Threading.Thread.Sleep(0);
        }
        if (dueñoVentana == 0) { try { lanzado.Kill(); } catch (InvalidOperationException) { } return new double[] { creada, visible, libre, -998 }; }
        Process p = dueñoVentana == pid ? lanzado : Process.GetProcessById((int)dueñoVentana);
        try { if (p.WaitForInputIdle(Math.Max(1, limiteMs - (int)reloj.ElapsedMilliseconds))) libre = reloj.Elapsed.TotalMilliseconds; } catch (InvalidOperationException) { }
        if (matar > 0) { System.Threading.Thread.Sleep(matar); try { p.Kill(); } catch (InvalidOperationException) { } p.WaitForExit(); return new double[] { creada, visible, libre, 0 }; }
        if (!p.WaitForExit(Math.Max(1, limiteMs - (int)reloj.ElapsedMilliseconds))) { p.Kill(); p.WaitForExit(); return new double[] { creada, visible, libre, -999 }; }
        // El código de salida de un proceso que no lanzó la sonda no se puede leer: se da por bueno si dejó su registro.
        return new double[] { creada, visible, libre, p == lanzado ? p.ExitCode : 0 };
    }
}
'@

function Argumentos-De([string]$registro) { if ($Matar -gt 0) { $Argumentos } else { $Argumentos + ' --registro "' + $registro + '"' } }

$carpeta = Split-Path -Parent $Exe
$nombre = Split-Path -Leaf $Exe
$temporal = Join-Path ([IO.Path]::GetTempPath()) ("rodaje-arranque-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$filas = @()
try {
    # Una vuelta de calentamiento en caliente: la primera tras compilar paga la verificación del antivirus.
    New-Item -ItemType Directory -Force -Path $temporal | Out-Null
    if (-not $Frio) { [void][SondaArranque]::Medir($Exe, (Argumentos-De (Join-Path $temporal 'x.txt')), 60000, $Matar) }
    for ($i = 1; $i -le $Veces; $i++) {
        $exeVuelta = $Exe
        if ($Frio) {
            $copia = Join-Path $temporal "c$i"
            & robocopy $carpeta $copia /E /J /NFL /NDL /NJH /NJS /NP /XF '*.pdb' 'rodaje-error.txt' | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy falló con $LASTEXITCODE" }
            $exeVuelta = Join-Path $copia $nombre
        }
        New-Item -ItemType Directory -Force -Path $temporal | Out-Null
        $registro = Join-Path $temporal "r$i.txt"
        $r = [SondaArranque]::Medir($exeVuelta, (Argumentos-De $registro), 60000, $Matar)
        $fila = [ordered]@{ creada = $r[0]; visible = $r[1]; cola_libre = $r[2]; salida = $r[3] }
        if (Test-Path -LiteralPath $registro) {
            foreach ($l in Get-Content -LiteralPath $registro -Encoding UTF8) {
                if ($l -match '^arranque\.(\w+)_ms: (\d+)') { $fila[$Matches[1]] = [double]$Matches[2] }
            }
        }
        if ($Matar -eq 0 -and -not $fila.Contains('interfaz_usable')) { $fila.salida = -1 }
        $filas += [pscustomobject]$fila
        if ($Frio) { Remove-Item -LiteralPath $copia -Recurse -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 400
    }
} finally {
    Remove-Item -LiteralPath $temporal -Recurse -Force -ErrorAction SilentlyContinue
}

$modo = if ($Frio) { 'frío' } else { 'caliente' }
Write-Host ''
Write-Host "  $Etiqueta, arranque en $modo, $Veces vueltas (ms; mediana, mínimo y máximo)" -ForegroundColor Cyan
$columnas = @($filas | ForEach-Object { $_.PSObject.Properties.Name } | Select-Object -Unique)
foreach ($c in $columnas) {
    if ($c -eq 'salida') { continue }
    $v = @($filas | ForEach-Object { $_.$c } | Where-Object { $_ -ne $null -and $_ -ge 0 } | Sort-Object)
    if ($v.Count -eq 0) { continue }
    Write-Host ("    {0,-22} {1,6:0} {2,6:0} {3,6:0}" -f $c, $v[[int][math]::Floor($v.Count / 2)], $v[0], $v[-1])
}
$malas = @($filas | Where-Object { $_.salida -ne 0 })
if ($malas.Count -gt 0) { Write-Host "    $($malas.Count) vuelta(s) no salieron con 0." -ForegroundColor Yellow }
exit ([int]($malas.Count -gt 0))
