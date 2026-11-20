using System.Runtime.InteropServices;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Si el monitor donde está la ventana tiene el HDR de Windows activo, por DisplayConfig
/// (<c>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO</c>): el camino de la ventana → <c>\\.\DISPLAYn</c> → ruta activa → destino.
/// <see cref="CompositionSwapChain.HasHdrOutput"/> mira todos los monitores del adaptador; esto, solo el de la ventana, así
/// «Visor HDR: Siempre» no quema la imagen en una pantalla SDR.
/// </summary>
internal static unsafe partial class DisplayHdr
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint QdcOnlyActivePaths = 2;
    private const int GetSourceName = 1;
    private const int GetAdvancedColorInfo = 9;
    private const uint AdvancedColorEnabled = 2;

    /// <summary><c>true</c> o <c>false</c> si DisplayConfig contestó por el monitor de <paramref name="hwnd"/>; <c>null</c> si no se pudo saber.</summary>
    public static bool? IsWindowMonitorInHdr(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        try
        {
            return Query(hwnd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool? Query(nint hwnd)
    {
        nint monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return null;
        }

        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (GetMonitorInfoW(monitor, &info) == 0)
        {
            return null;
        }

        string device = new((char*)info.szDevice);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
            {
                return null;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(modeCount, 1)];
            int result;
            fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, 0);
            }

            if (result == 122) // ERROR_INSUFFICIENT_BUFFER: cambió la topología entre las dos llamadas.
            {
                continue;
            }

            if (result != 0)
            {
                return null;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = GetSourceName,
                        size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                        adapterLow = paths[i].sourceAdapterLow,
                        adapterHigh = paths[i].sourceAdapterHigh,
                        id = paths[i].sourceId,
                    },
                };
                if (DisplayConfigGetDeviceInfo(&name) != 0 || !string.Equals(new string((char*)name.viewGdiDeviceName), device, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = GetAdvancedColorInfo,
                        size = (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO),
                        adapterLow = paths[i].targetAdapterLow,
                        adapterHigh = paths[i].targetAdapterHigh,
                        id = paths[i].targetId,
                    },
                };
                if (DisplayConfigGetDeviceInfo(&color) != 0)
                {
                    return null;
                }

                return (color.value & AdvancedColorEnabled) != 0;
            }

            return null;
        }

        return null;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount, DISPLAYCONFIG_PATH_INFO* paths, ref uint modeCount,
        DISPLAYCONFIG_MODE_INFO* modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(void* packet);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed ushort szDevice[32];
    }

    // DISPLAYCONFIG_PATH_INFO de wingdi.h, con los LUID y las uniones aplanados: 72 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public uint sourceAdapterLow;
        public int sourceAdapterHigh;
        public uint sourceId;
        public uint sourceModeInfoIdx;
        public uint sourceStatusFlags;
        public uint targetAdapterLow;
        public int targetAdapterHigh;
        public uint targetId;
        public uint targetModeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint targetStatusFlags;
        public uint flags;
    }

    // DISPLAYCONFIG_MODE_INFO: cabecera de 16 bytes y una unión de 48 (DISPLAYCONFIG_TARGET_MODE es la mayor): 64 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public uint adapterLow;
        public int adapterHigh;
        public fixed byte body[48];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public uint adapterLow;
        public int adapterHigh;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public fixed ushort viewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>Bit 0: admite color avanzado; bit 1: activo; bit 2: color amplio forzado; bit 3: desactivado a la fuerza.</summary>
        public uint value;

        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }
}
