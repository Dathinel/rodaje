using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Media.Gpu;

/// <summary>
/// Sobre qué adaptador se crean los dispositivos de D3D11 (visor, exportación, perfil de la GPU). En un portátil híbrido
/// (Intel integrada + NVIDIA) el adaptador por defecto suele ser la integrada; en automático se pide el de alto
/// rendimiento a DXGI, salvo que la persona haya elegido «Ahorro de energía» para Rodaje en Configuración de Windows ›
/// Pantalla › Gráficos (para el lanzador o para <c>app\Rodaje.exe</c>) o un adaptador por nombre en Preferencias › Rendimiento.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public static unsafe class GpuAdapterChoice
{
    /// <summary>Parte del nombre del adaptador que hay que usar (para medir con cada uno); manda sobre Preferencias.</summary>
    public const string Variable = "RODAJE_ADAPTADOR";

    private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public enum Mode
    {
        /// <summary><c>EnumAdapterByGpuPreference(HIGH_PERFORMANCE)</c>.</summary>
        HighPerformance,

        /// <summary><c>EnumAdapterByGpuPreference(MINIMUM_POWER)</c>: lo eligió la persona en Windows.</summary>
        MinimumPower,

        /// <summary>El primero de hardware cuyo nombre contiene el pedido.</summary>
        Named,
    }

    /// <summary>Parte del nombre elegida en Preferencias › Rendimiento; <c>null</c> o vacía, automática. Se aplica al crear el siguiente dispositivo.</summary>
    public static string? Preferred { get; set; }

    /// <summary>
    /// Qué hacer con un nombre pedido (variable o Preferencias) y el valor de Windows para este exe
    /// (<c>GpuPreference=N;</c> en <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>).
    /// </summary>
    public static Mode Decide(string? name, string? windowsPreference) =>
        !string.IsNullOrWhiteSpace(name) ? Mode.Named
        : WindowsPowerSaving(windowsPreference) ? Mode.MinimumPower
        : Mode.HighPerformance;

    /// <summary><c>GpuPreference=1</c> es «Ahorro de energía»; <c>0</c> («que decida Windows») y <c>2</c> van a la potente.</summary>
    public static bool WindowsPowerSaving(string? value)
    {
        foreach (string part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            {
                return part.AsSpan("GpuPreference=".Length) is "1";
            }
        }

        return false;
    }

    /// <summary>
    /// El adaptador elegido, con una referencia que libera quien lo recibe; <c>null</c> para dejar que D3D11 tome el de por
    /// defecto (sin DXGI 1.6, o solo hay software).
    /// </summary>
    internal static IDXGIAdapter1* Open()
    {
        string? name = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } variable ? variable : Preferred;
        Mode mode = Decide(name, ReadWindowsPreference());
        IDXGIFactory1* factory;
        if (CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory).FAILED)
        {
            return null;
        }

        try
        {
            IDXGIAdapter1* adapter;
            if (mode == Mode.Named)
            {
                for (uint i = 0; factory->EnumAdapters1(i, &adapter).SUCCEEDED; i++)
                {
                    if (IsHardware(adapter, out string description) && description.Contains(name!.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return adapter;
                    }

                    adapter->Release();
                }
            }

            // Automática, o el nombre pedido ya no está (otra GPU, otro equipo): el de alto rendimiento.
            IDXGIFactory6* factory6;
            if (factory->QueryInterface(__uuidof<IDXGIFactory6>(), (void**)&factory6).FAILED)
            {
                return null;
            }

            DXGI_GPU_PREFERENCE preference = mode == Mode.MinimumPower
                ? DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_MINIMUM_POWER
                : DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE;
            HRESULT hr = factory6->EnumAdapterByGpuPreference(0, preference, __uuidof<IDXGIAdapter1>(), (void**)&adapter);
            factory6->Release();
            if (hr.FAILED)
            {
                return null;
            }

            if (IsHardware(adapter, out _))
            {
                return adapter;
            }

            adapter->Release();
            return null;
        }
        finally
        {
            factory->Release();
        }
    }

    /// <summary>Nombre del adaptador que usará el siguiente dispositivo; <c>null</c> si será el de por defecto.</summary>
    public static string? Describe()
    {
        IDXGIAdapter1* adapter = Open();
        if (adapter == null)
        {
            return null;
        }

        IsHardware(adapter, out string description);
        adapter->Release();
        return description;
    }

    private static bool IsHardware(IDXGIAdapter1* adapter, out string description)
    {
        DXGI_ADAPTER_DESC1 desc;
        if (adapter->GetDesc1(&desc).FAILED)
        {
            description = "";
            return false;
        }

        description = new string((char*)&desc.Description).TrimEnd('\0');
        return (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) == 0;
    }

    // La de app\Rodaje.exe o, si no tiene, la del lanzador de la carpeta de arriba (el exe que la persona ve y elige).
    private static string? ReadWindowsPreference()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey);
            if (key is null || Environment.ProcessPath is not { } exe)
            {
                return null;
            }

            string? launcher = Path.GetDirectoryName(Path.GetDirectoryName(exe)) is { } up ? Path.Combine(up, Path.GetFileName(exe)) : null;
            return key.GetValue(exe) as string ?? (launcher is null ? null : key.GetValue(launcher) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
