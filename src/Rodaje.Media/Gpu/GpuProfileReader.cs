using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Media.Gpu;

/// <summary>
/// Lee el <see cref="GpuProfile"/> del equipo: los adaptadores de DXGI (fabricante, modelo, VRAM, controlador) y los
/// perfiles de decodificación que declara el principal. Lo guarda en <c>gpu-perfil.json</c> de la carpeta de datos con
/// la clave de los adaptadores y sus controladores: si nada cambió, no vuelve a abrir la GPU para enumerar perfiles.
/// </summary>
public static unsafe class GpuProfileReader
{
    private const string CacheFileName = "gpu-perfil.json";

    private static readonly Lazy<GpuProfile> Shared = new(() => Read(DefaultCachePath()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>El perfil de este equipo, leído una vez por proceso.</summary>
    public static GpuProfile Current => Shared.Value;

    /// <summary>Lee el perfil; con <paramref name="cachePath"/> usa y actualiza la caché. Nunca lanza: sin DXGI, todo por CPU.</summary>
    public static GpuProfile Read(string? cachePath)
    {
        int cores = Environment.ProcessorCount;
        IReadOnlyList<GpuAdapter> adapters;
        try
        {
            adapters = EnumerateAdapters();
        }
#pragma warning disable CA1031 // Sin DXGI (sesión remota rara, servidor) se sigue por CPU.
        catch (Exception)
#pragma warning restore CA1031
        {
            return GpuProfile.CpuOnly(cores);
        }

        var profile = new GpuProfile(adapters, HardwareDecodeProfiles.None, cores);
        if (profile.Primary is null)
        {
            return profile;
        }

        if (cachePath is not null && TryReadCache(cachePath, profile.Key) is { } cached)
        {
            return profile with { Decode = cached };
        }

        HardwareDecodeProfiles decode = ReadPrimaryDecodeProfiles();
        if (cachePath is not null)
        {
            TryWriteCache(cachePath, profile.Key, decode);
        }

        return profile with { Decode = decode };
    }

    /// <summary><c>%LOCALAPPDATA%\Rodaje\gpu-perfil.json</c>, o el de <c>RODAJE_DATOS</c>.</summary>
    public static string DefaultCachePath()
    {
        string folder = EnvironmentVariables.Read(EnvironmentVariables.Datos)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje");
        return Path.Combine(folder, CacheFileName);
    }

    /// <summary>Los perfiles de decodificación que declara un dispositivo con soporte de video, comprobando el formato de salida.</summary>
    internal static HardwareDecodeProfiles ReadDecodeProfiles(ID3D11Device* device)
    {
        ID3D11VideoDevice* video;
        if (device->QueryInterface(__uuidof<ID3D11VideoDevice>(), (void**)&video).FAILED)
        {
            return HardwareDecodeProfiles.None;
        }

        var result = HardwareDecodeProfiles.None;
        try
        {
            uint count = video->GetVideoDecoderProfileCount();
            for (uint i = 0; i < count; i++)
            {
                Guid guid;
                if (video->GetVideoDecoderProfile(i, &guid).FAILED)
                {
                    continue;
                }

                HardwareDecodeProfiles profile = HardwareDecodeSupport.FromGuid(guid);
                if (profile == HardwareDecodeProfiles.None)
                {
                    continue;
                }

                DXGI_FORMAT format = profile is HardwareDecodeProfiles.HevcMain10 or HardwareDecodeProfiles.Vp9Profile2
                    ? DXGI_FORMAT.DXGI_FORMAT_P010
                    : DXGI_FORMAT.DXGI_FORMAT_NV12;
                BOOL supported;
                if (video->CheckVideoDecoderFormat(&guid, format, &supported).SUCCEEDED && supported)
                {
                    result |= profile;
                }
            }
        }
        finally
        {
            video->Release();
        }

        return result;
    }

    private static List<GpuAdapter> EnumerateAdapters()
    {
        IDXGIFactory1* factory;
        GpuDevice.Check(CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory), "CreateDXGIFactory1");
        var adapters = new List<GpuAdapter>();
        try
        {
            IDXGIAdapter1* adapter;
            for (uint i = 0; factory->EnumAdapters1(i, &adapter).SUCCEEDED; i++)
            {
                try
                {
                    DXGI_ADAPTER_DESC1 desc;
                    if (adapter->GetDesc1(&desc).FAILED)
                    {
                        continue;
                    }

                    LARGE_INTEGER version;
                    string? driver = adapter->CheckInterfaceSupport(__uuidof<IDXGIDevice>(), &version).SUCCEEDED
                        ? FormatDriverVersion(version.QuadPart)
                        : null;
                    bool software = (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                    adapters.Add(new GpuAdapter(new string((char*)&desc.Description).TrimEnd('\0'), desc.VendorId, desc.DeviceId,
                        (long)desc.DedicatedVideoMemory, (long)desc.SharedSystemMemory, software, driver));
                }
                finally
                {
                    adapter->Release();
                }
            }
        }
        finally
        {
            factory->Release();
        }

        return adapters;
    }

    // El adaptador de por defecto, como GpuDevice: es el que decodifica en Rodaje.
    private static HardwareDecodeProfiles ReadPrimaryDecodeProfiles()
    {
        ID3D11Device* device = null;
        D3D_FEATURE_LEVEL level;
        HRESULT hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL,
            (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT, null, 0, D3D11_SDK_VERSION, &device, &level, null);
        if (hr.FAILED)
        {
            return HardwareDecodeProfiles.None;
        }

        try
        {
            return ReadDecodeProfiles(device);
        }
        finally
        {
            device->Release();
        }
    }

    internal static string FormatDriverVersion(long umd) => string.Create(CultureInfo.InvariantCulture,
        $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}");

    private static HardwareDecodeProfiles? TryReadCache(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            GpuProfileCacheEntry? entry = JsonSerializer.Deserialize(File.ReadAllText(path), GpuProfileJsonContext.Default.GpuProfileCacheEntry);
            return entry is not null && entry.Version == GpuProfileCacheEntry.CurrentVersion && string.Equals(entry.Key, key, StringComparison.Ordinal)
                ? (HardwareDecodeProfiles)entry.Decode
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryWriteCache(string path, string key, HardwareDecodeProfiles decode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new GpuProfileCacheEntry { Key = key, Decode = (int)decode }, GpuProfileJsonContext.Default.GpuProfileCacheEntry));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin caché solo cuesta volver a enumerar la próxima vez.
        }
    }
}

internal sealed class GpuProfileCacheEntry
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Key { get; set; } = "";

    public int Decode { get; set; }
}

[JsonSerializable(typeof(GpuProfileCacheEntry))]
internal sealed partial class GpuProfileJsonContext : JsonSerializerContext;
