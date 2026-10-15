using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Contrato de «Memoria compartida» en <c>docs/protocolo-modulos.md</c> (tarea 355): una prueba por promesa, con su frase
/// encima. Las que usan regiones de verdad solo corren en Windows (en Linux no hay <c>MemoryMappedFile</c> con nombre);
/// las capacidades son múltiplos de 4096, como en <c>RegionFuzzTests</c>.
/// </summary>
public sealed class SharedMemoryContractTests
{
    private const string SomeRegion = "Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e";

    // «Las regiones las crea siempre el editor (`ModuleClient.CreateRegion(capacidad)`): un mapeo de archivo con nombre de
    // Windows, `Local\Rodaje.Mod.<guid>` (el guid en 32 cifras hexadecimales; `SharedMemoryRegion.NamePrefix = "Rodaje.Mod."`).»
    [Fact]
    public async Task Editor_regions_are_named_Local_Rodaje_Mod_plus_32_hex_digits()
    {
        Assert.Equal("Rodaje.Mod.", SharedMemoryRegion.NamePrefix);
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            SharedMemoryRegion region = client.CreateRegion(4096);
            Assert.Matches(new Regex("^Rodaje\\.Mod\\.[0-9a-f]{32}$"), region.Name);
            Assert.Equal(4096, region.Capacity);
            using MemoryMappedFile mapped = MemoryMappedFile.OpenExisting(@"Local\" + region.Name);
            Assert.NotEqual(region.Name, client.CreateRegion(4096).Name);
        }
    }

    // «En los mensajes, `region` es el nombre **sin** `Local\`.»
    [Fact]
    public async Task Buffers_name_the_region_without_Local()
    {
        Assert.False(SharedMemoryRegion.IsValidName(@"Local\" + SomeRegion));
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            SharedMemoryRegion region = client.CreateRegion(4096);
            Assert.Equal(region.Name, region.Describe(0, 16).Region);
        }
    }

    // «La región vive hasta que el editor la libera (`ReleaseRegion`) o cierra el cliente.»
    [Fact]
    public async Task A_region_lives_until_released_or_until_the_client_closes()
    {
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        SharedMemoryRegion released, kept;
        await using (client)
        {
            released = client.CreateRegion(4096);
            kept = client.CreateRegion(4096);
            client.ReleaseRegion(released);
            Assert.True(released.IsDisposed);
            Assert.Equal(ErrorCodes.BufferOutOfRange, Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open(released.Name)).Code);
            Assert.False(kept.IsDisposed);
            using (SharedMemoryRegion open = SharedMemoryRegion.Open(kept.Name)) Assert.Equal(4096, open.Capacity);
        }
        Assert.True(kept.IsDisposed);
        Assert.True(await host.WaitAsync(Support.CiBound));
        Assert.Equal(ErrorCodes.BufferOutOfRange, Assert.Throws<ModuleCallException>(() => SharedMemoryRegion.Open(kept.Name)).Code);
    }

    // «El módulo la abre por nombre con permiso de lectura y escritura (`MemoryMappedFile.OpenExisting(@"Local\" + nombre,
    // ReadWrite)` en `SharedMemoryRegion.Open`).»
    [Fact]
    public async Task The_module_opens_the_region_by_name_for_reading_and_writing()
    {
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            SharedMemoryRegion region = client.CreateRegion(4096);
            region.GetSpan(0, 4).Fill(7);
            using SharedMemoryRegion module = SharedMemoryRegion.Open(region.Name);
            Assert.Equal(region.Name, module.Name);
            Assert.Equal(4096, module.Capacity);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, module.GetSpan(0, 4).ToArray());
            module.GetSpan(4096 - 2, 2).Fill(9);
            Assert.Equal(new byte[] { 9, 9 }, region.GetSpan(4096 - 2, 2).ToArray());
        }
    }

    // «En C#, `ModuleCall.Map(búfer)` hace todo: abre la región la primera vez (y la guarda abierta mientras dure la
    // conexión), comprueba los límites y devuelve un `Span<byte>`.»
    // Se ve en que el módulo sigue alcanzando la región aunque el editor ya la liberó: si la hubiera cerrado al terminar la
    // llamada, nadie tendría el mapeo abierto y abrirla de nuevo fallaría. Al cerrar la conexión, el módulo la suelta.
    [Fact]
    public async Task Map_opens_the_region_once_and_keeps_it_open_while_the_connection_lasts()
    {
        if (!OperatingSystem.IsWindows()) return;
        ModuleHost host = Support.EchoHost();
        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(host);
        SharedMemoryRegion moduleSide;
        await using (client)
        {
            SharedMemoryRegion region = client.CreateRegion(4096);
            SharedBufferRef from = region.Describe(0, 8), to = region.Describe(8, 8);
            region.GetSpan(from).Fill(5);
            await client.InvokeAsync("copiar", buffers: [from, to]);
            Assert.Equal(new byte[] { 5, 5, 5, 5, 5, 5, 5, 5 }, region.GetSpan(to).ToArray());
            client.ReleaseRegion(region);

            moduleSide = host.Region(region.Name);
            Assert.Same(moduleSide, host.Region(region.Name));
            Assert.False(moduleSide.IsDisposed);
            Assert.Equal(new byte[] { 5, 5, 5, 5, 5, 5, 5, 5 }, moduleSide.GetSpan(to).ToArray());
        }
        Assert.True(await run.WaitAsync(Support.CiBound));
        Assert.True(moduleSide.IsDisposed);
    }

    // «Cada acceso comprueba los límites contra el tamaño real de la región: un búfer que se sale da `buffer-out-of-range`
    // («Búfer *desplazamiento*+*largo* fuera de la región de *N* bytes.», `SharedMemoryRegion.GetSpan`).»
    [Fact]
    public async Task An_access_outside_the_region_is_buffer_out_of_range_with_the_documented_message()
    {
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            SharedMemoryRegion region = client.CreateRegion(8192);
            Assert.Equal(16, region.GetSpan(8192 - 16, 16).Length);
            ModuleCallException editor = Assert.Throws<ModuleCallException>(() => region.GetSpan(8190, 16));
            Assert.Equal(ErrorCodes.BufferOutOfRange, editor.Code);
            Assert.Equal("Búfer 8190+16 fuera de la región de 8192 bytes.", editor.Message);

            SharedBufferRef inside = region.Describe(0, 16);
            SharedBufferRef outside = new(region.Name, 8190, 16);
            ModuleCallException module = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("copiar", buffers: [outside, inside]));
            Assert.Equal(ErrorCodes.BufferOutOfRange, module.Code);
            Assert.Equal("Búfer 8190+16 fuera de la región de 8192 bytes.", module.Message);
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
    }

    // «Lo que devuelve en `result.buffers` tiene que estar dentro de una región que creó el editor en esa conexión y dentro
    // de su capacidad, o el editor corta la conexión.»
    [Fact]
    public async Task A_result_buffer_in_a_region_the_editor_did_not_create_cuts_the_connection()
    {
        ModuleClient client = await HostileResultAsync(() => Task.FromResult("""{"region":"Rodaje.Mod.ajena","offset":0,"length":4}"""));
        await using (client)
        {
            await Assert.ThrowsAsync<ModuleProtocolException>(() => client.InvokeAsync("eco"));
            await Assert.ThrowsAnyAsync<ModuleException>(() => client.InvokeAsync("eco"));
        }
    }

    [Fact]
    public async Task A_result_buffer_in_a_region_of_another_connection_cuts_the_connection()
    {
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient other, _) = await Support.ConnectInProcessAsync();
        await using (other)
        {
            SharedMemoryRegion foreign = other.CreateRegion(4096);
            ModuleClient client = await HostileResultAsync(() => Task.FromResult($$"""{"region":"{{foreign.Name}}","offset":0,"length":4}"""));
            await using (client)
            {
                await Assert.ThrowsAsync<ModuleProtocolException>(() => client.InvokeAsync("eco"));
                await Assert.ThrowsAnyAsync<ModuleException>(() => client.InvokeAsync("eco"));
            }
        }
    }

    [Fact]
    public async Task A_result_buffer_past_the_capacity_cuts_the_connection_and_one_at_the_edge_does_not()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach ((long offset, bool inside) in new[] { (4096L - 4, true), (4096L - 3, false) })
        {
            TaskCompletionSource<string> name = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ModuleClient client = await HostileResultAsync(async () => $$"""{"region":"{{await name.Task}}","offset":{{offset}},"length":4}""");
            await using (client)
            {
                name.SetResult(client.CreateRegion(4096).Name);
                if (inside) Assert.Equal(offset, Assert.Single((await client.InvokeAsync("eco")).Buffers).Offset);
                else await Assert.ThrowsAsync<ModuleProtocolException>(() => client.InvokeAsync("eco"));
            }
        }
    }

    // «El editor solo puede nombrar en un `invoke` regiones que creó con ese cliente (`ModuleClient.InvokeAsync` lanza
    // `ArgumentException` si no).»
    [Fact]
    public async Task Invoke_refuses_regions_the_client_did_not_create()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync("eco", buffers: [new SharedBufferRef(SomeRegion, 0, 4)]));
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task Invoke_refuses_regions_of_another_client_and_released_ones()
    {
        if (!OperatingSystem.IsWindows()) return;
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        (ModuleClient other, _) = await Support.ConnectInProcessAsync();
        await using (client)
        await using (other)
        {
            SharedMemoryRegion foreign = other.CreateRegion(4096);
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync("eco", buffers: [foreign.Describe(0, 4)]));
            SharedMemoryRegion released = client.CreateRegion(4096);
            SharedBufferRef buffer = released.Describe(0, 4);
            client.ReleaseRegion(released);
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync("eco", buffers: [buffer]));
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
    }

    // «`region` | Nombre de la región: de 1 a 128 caracteres entre letras y dígitos ASCII, `.`, `-` y `_`.»
    [Theory]
    [InlineData("a", true)]
    [InlineData("Rodaje.Mod.A-z_0.9", true)]
    [InlineData("", false)]
    [InlineData("Rodaje Mod", false)]
    [InlineData("Rodaje/Mod", false)]
    [InlineData("Rodaje:Mod", false)]
    [InlineData("Rodaje.Mod.ñ", false)]
    [InlineData("Rodaje.Mod.١", false)]
    public void Region_names_use_ascii_letters_digits_dot_dash_and_underscore(string name, bool valid)
    {
        Assert.Equal(valid, new SharedBufferRef(name, 0, 4).Validate() is null);
    }

    [Fact]
    public void Region_names_have_1_to_128_characters()
    {
        Assert.Null(new SharedBufferRef(new string('a', 128), 0, 4).Validate());
        Assert.NotNull(new SharedBufferRef(new string('a', 129), 0, 4).Validate());
    }

    // «`offset` | Desplazamiento en bytes desde el inicio de la región, ≥ 0.»
    // «`length` | Largo en bytes, de 1 a 2 147 483 647 (`int.MaxValue`); `offset + length` no puede desbordar.»
    [Theory]
    [InlineData(0L, 1L, true)]
    [InlineData(-1L, 1L, false)]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, -1L, false)]
    [InlineData(0L, int.MaxValue, true)]
    [InlineData(0L, int.MaxValue + 1L, false)]
    [InlineData(long.MaxValue - int.MaxValue, int.MaxValue, true)]
    [InlineData(long.MaxValue - int.MaxValue + 1, int.MaxValue, false)]
    [InlineData(long.MaxValue, 1L, false)]
    public void Offset_and_length_ranges(long offset, long length, bool valid)
    {
        Assert.Equal(valid, new SharedBufferRef(SomeRegion, offset, length).Validate() is null);
    }

    // «Un búfer es de video, de audio o de bytes sueltos (sin `video` ni `audio`), nunca de los dos.»
    [Fact]
    public void A_buffer_is_video_audio_or_bytes_but_never_both()
    {
        VideoLayout video = new(2, 2, 8, PixelFormats.Rgba8);
        AudioLayout audio = new(48000, 1, 4);
        Assert.Null(new SharedBufferRef(SomeRegion, 0, 64).Validate());
        Assert.Null(new SharedBufferRef(SomeRegion, 0, 64, video).Validate());
        Assert.Null(new SharedBufferRef(SomeRegion, 0, 64, Audio: audio).Validate());
        Assert.NotNull(new SharedBufferRef(SomeRegion, 0, 64, video, audio).Validate());
    }

    // «Como mucho 16 búferes por mensaje.»
    [Fact]
    public void At_most_16_buffers_per_message()
    {
        SharedBufferRef[] sixteen = [.. Enumerable.Range(0, 16).Select(i => new SharedBufferRef(SomeRegion, i * 4L, 4))];
        SharedBufferRef[] seventeen = [.. sixteen, new SharedBufferRef(SomeRegion, 64, 4)];
        Assert.Null(new InvokeMessage(1, "eco", Buffers: sixteen).Validate());
        Assert.NotNull(new InvokeMessage(1, "eco", Buffers: seventeen).Validate());
        Assert.Null(new ResultMessage(1, Buffers: sixteen).Validate());
        Assert.NotNull(new ResultMessage(1, Buffers: seventeen).Validate());
    }

    // «**Video** (`VideoLayout`): `width` y `height` de 1 a 16 384»
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(16384, 1, true)]
    [InlineData(1, 16384, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(16385, 1, false)]
    [InlineData(1, 16385, false)]
    public void Video_width_and_height_go_from_1_to_16384(int width, int height, bool valid)
    {
        SharedBufferRef buffer = new(SomeRegion, 0, (long)width * height, new VideoLayout(width, height, width, PixelFormats.Gray8));
        Assert.Equal(valid, buffer.Validate() is null);
    }

    // «`stride` (bytes por fila) ≥ `width` × bytes por píxel; `stride` × `height` ≤ `length`.»
    // «| `rgba8` | 4 | `bgra8` | 4 | `rgba16f` | 8 | `gray8` | 1 |»
    [Theory]
    [InlineData(PixelFormats.Rgba8, 4)]
    [InlineData(PixelFormats.Bgra8, 4)]
    [InlineData(PixelFormats.Rgba16F, 8)]
    [InlineData(PixelFormats.Gray8, 1)]
    public void Video_stride_and_length_follow_the_bytes_per_pixel(string format, int bytesPerPixel)
    {
        Assert.Equal(bytesPerPixel, PixelFormats.BytesPerPixel(format));
        const int width = 10, height = 3;
        int stride = width * bytesPerPixel;
        Assert.Null(new SharedBufferRef(SomeRegion, 0, stride * height, new VideoLayout(width, height, stride, format)).Validate());
        Assert.NotNull(new SharedBufferRef(SomeRegion, 0, stride * height, new VideoLayout(width, height, stride - 1, format)).Validate());
        Assert.NotNull(new SharedBufferRef(SomeRegion, 0, stride * height - 1, new VideoLayout(width, height, stride, format)).Validate());
        Assert.Null(new SharedBufferRef(SomeRegion, 0, (stride + 6) * height, new VideoLayout(width, height, stride + 6, format)).Validate());
    }

    [Theory]
    [InlineData("rgb8")]
    [InlineData("RGBA8")]
    [InlineData("nv12")]
    [InlineData("")]
    public void Other_pixel_formats_are_refused(string format)
    {
        Assert.Equal(0, PixelFormats.BytesPerPixel(format));
        Assert.NotNull(new SharedBufferRef(SomeRegion, 0, 4096, new VideoLayout(4, 4, 32, format)).Validate());
    }

    // «**Audio** (`AudioLayout`): muestras `f32` **intercaladas** (`sampleFormat`, el único valor admitido, `SampleFormats.F32`)»
    [Theory]
    [InlineData(SampleFormats.F32, true)]
    [InlineData("F32", false)]
    [InlineData("s16", false)]
    [InlineData("f64", false)]
    public void Audio_admits_only_f32(string format, bool valid)
    {
        Assert.Equal("f32", SampleFormats.F32);
        Assert.Equal(valid, new SharedBufferRef(SomeRegion, 0, 4096, Audio: new AudioLayout(48000, 2, 16, format)).Validate() is null);
    }

    // «`sampleRate` de 8000 a 384 000; `channels` de 1 a 32; `frames` (muestras por canal) ≥ 0»
    [Theory]
    [InlineData(8000, 1, 0L, true)]
    [InlineData(384000, 32, 1L, true)]
    [InlineData(7999, 1, 1L, false)]
    [InlineData(384001, 1, 1L, false)]
    [InlineData(48000, 0, 1L, false)]
    [InlineData(48000, 33, 1L, false)]
    [InlineData(48000, 2, -1L, false)]
    public void Audio_rate_channels_and_frames_ranges(int sampleRate, int channels, long frames, bool valid)
    {
        Assert.Equal(valid, new SharedBufferRef(SomeRegion, 0, 4096, Audio: new AudioLayout(sampleRate, channels, frames)).Validate() is null);
    }

    // «`frames` × `channels` × 4 ≤ `length`»
    [Theory]
    [InlineData(1, 100L)]
    [InlineData(2, 48000L)]
    [InlineData(32, 3L)]
    public void Audio_fits_in_frames_times_channels_times_4_bytes(int channels, long frames)
    {
        long bytes = frames * channels * 4;
        Assert.Null(new SharedBufferRef(SomeRegion, 0, bytes, Audio: new AudioLayout(48000, channels, frames)).Validate());
        Assert.NotNull(new SharedBufferRef(SomeRegion, 0, bytes - 1, Audio: new AudioLayout(48000, channels, frames)).Validate());
    }

    // «Ejemplo del serializador, 1 s estéreo a 48 kHz:»
    [Fact]
    public void The_documented_audio_invoke_is_what_the_serializer_writes()
    {
        const string documented =
            """{"type":"invoke","id":10,"function":"ruido","buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":0,"length":384000,"audio":{"sampleRate":48000,"channels":2,"frames":48000,"sampleFormat":"f32"}}]}""";
        InvokeMessage message = new(10, "ruido", Buffers: [new SharedBufferRef(SomeRegion, 0, 384000, Audio: new AudioLayout(48000, 2, 48000))]);
        Assert.Null(message.Validate());
        Assert.Equal(documented, JsonSerializer.Serialize<ModuleMessage>(message, ProtocolJsonContext.Default.ModuleMessage));
        Assert.Equal(documented, JsonSerializer.Serialize(MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(documented)), ProtocolJsonContext.Default.ModuleMessage));
    }

    /// <summary>Un módulo hostil que contesta el primer <c>invoke</c> con un <c>result</c> que trae el búfer de <paramref name="buffer"/>.</summary>
    private static Task<ModuleClient> HostileResultAsync(Func<Task<string>> buffer) => Support.HostileAsync(async module =>
    {
        if (await Support.ReadFrameAsync(module) is not { } invoke) return;
        long id = JsonDocument.Parse(invoke).RootElement.GetProperty("id").GetInt64();
        await module.WriteAsync(Support.Frame($$"""{"type":"result","id":{{id}},"buffers":[{{await buffer()}}]}"""));
        while (await Support.ReadFrameAsync(module) is not null) { }
    });
}
