using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

public sealed class MessageTests
{
    public static TheoryData<ModuleMessage> AllMessages() =>
    [
        new HelloMessage(1, 2, "rodaje", "0.7.0", "es-CO"),
        new WelcomeMessage(1, Support.EchoInfo),
        new InvokeMessage(7, "eco", Support.Json("""{"texto":"hola ñandú","n":[1,2.5,null]}"""),
            [new SharedBufferRef("Rodaje.Mod.abc", 0, 1920 * 1080 * 4, new VideoLayout(1920, 1080, 1920 * 4, PixelFormats.Rgba8))]),
        new ProgressMessage(7, 0.5, "etapa"),
        new ResultMessage(7, Support.Json("[1,2,3]"), [new SharedBufferRef("Rodaje.Mod.abc", 64, 48000 * 8, Audio: new AudioLayout(48000, 2, 48000))]),
        new ErrorMessage(ErrorCodes.Busy, "Ocupado.", 7),
        new ErrorMessage(ErrorCodes.Malformed, "Mal."),
        new CancelMessage(7),
        new PingMessage(3),
        new PongMessage(3),
        new LogMessage("info", "línea"),
        new ShutdownMessage(),
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public async Task Every_message_survives_a_round_trip(ModuleMessage message)
    {
        using MemoryStream stream = new();
        using (MessageChannel writer = new(new NonClosing(stream))) await writer.SendAsync(message);
        stream.Position = 0;
        using MessageChannel reader = new(stream);
        ModuleMessage? back = await reader.ReceiveAsync();
        Assert.NotNull(back);
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Serialize(message), Serialize(back));
        Assert.Null(await reader.ReceiveAsync());
    }

    [Fact]
    public void Type_can_come_after_other_properties()
    {
        Assert.Equal(new CancelMessage(3), MessageChannel.Parse("""{"id":3,"type":"cancel"}"""u8));
    }

    [Fact]
    public async Task Unknown_message_types_are_skipped_for_forward_compatibility()
    {
        using MemoryStream stream = new([.. Support.Frame("""{"type":"futuro","x":1}"""), .. Support.Frame("""{"type":"ping","id":1}""")]);
        using MessageChannel reader = new(stream);
        Assert.Equal(new PingMessage(1), await reader.ReceiveAsync());
        Assert.Equal(1, reader.SkippedUnknown);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        Assert.Equal(new PingMessage(2), MessageChannel.Parse("""{"type":"ping","id":2,"nuevo":{"a":[1]}}"""u8));
    }

    public static TheoryData<string> Malformed() =>
    [
        "",
        "no es json",
        "[]",
        "null",
        """{"type":"cancel"}""",
        """{"type":"cancel","id":"3"}""",
        """{"type":"cancel","id":0}""",
        """{"type":"cancel","id":-5}""",
        """{"type":"cancel","id":1e400}""",
        """{"type":"progress","id":1,"fraction":1.5}""",
        """{"type":"progress","id":1,"fraction":-0.1}""",
        """{"type":"invoke","id":1,"function":"Eco"}""",
        """{"type":"invoke","id":1,"function":"eco\u0000"}""",
        """{"type":"invoke","id":1,"function":"../eco"}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[null]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Rodaje.Mod.a","offset":-1,"length":4}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Local\\x","offset":0,"length":4}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"r","offset":9223372036854775000,"length":4096}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"r","offset":0,"length":100,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"r","offset":0,"length":99999999,"video":{"width":1920,"height":1080,"stride":10,"pixelFormat":"rgba8"}}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"r","offset":0,"length":99999999,"video":{"width":4,"height":4,"stride":16,"pixelFormat":"yuv9"}}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"r","offset":0,"length":8,"audio":{"sampleRate":48000,"channels":2,"frames":48000,"sampleFormat":"f32"}}]}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[null]}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[{"id":"a","name":"A"},{"id":"a","name":"B"}]}}""",
        """{"type":"hello","minProtocol":3,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""",
        """{"type":"error","id":1,"code":"","message":"x"}""",
    ];

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_messages_are_rejected_with_a_protocol_error(string json)
    {
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        byte[] json = [.. """{"type":"log","level":"info","text":" """u8, 0xC3, 0x28, .. "\"}"u8];
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(json));
    }

    [Fact]
    public void Deep_nesting_is_rejected()
    {
        string deep = new string('[', 200) + new string(']', 200);
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(Encoding.UTF8.GetBytes($$"""{"type":"invoke","id":1,"function":"eco","parameters":{{deep}}}""")));
    }

    [Fact]
    public void Long_texts_are_rejected()
    {
        string text = new('x', 5000);
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(Encoding.UTF8.GetBytes($$"""{"type":"log","level":"info","text":"{{text}}"}""")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(ProtocolVersion.MaxFrameBytes + 1)]
    public async Task Frame_lengths_out_of_range_are_rejected_without_allocating(int length)
    {
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using MessageChannel reader = new(new MemoryStream(header));
        await Assert.ThrowsAsync<ModuleProtocolException>(async () => await reader.ReceiveAsync());
    }

    [Fact]
    public async Task A_truncated_frame_is_an_error_but_a_clean_end_is_not()
    {
        byte[] frame = Support.Frame("""{"type":"ping","id":1}""");
        using MessageChannel truncated = new(new MemoryStream(frame[..^3]));
        await Assert.ThrowsAsync<ModuleProtocolException>(async () => await truncated.ReceiveAsync());
        using MessageChannel halfHeader = new(new MemoryStream(frame[..2]));
        await Assert.ThrowsAsync<ModuleProtocolException>(async () => await halfHeader.ReceiveAsync());
        using MessageChannel empty = new(new MemoryStream());
        Assert.Null(await empty.ReceiveAsync());
    }

    [Fact]
    public async Task Oversized_messages_are_not_sent()
    {
        using MessageChannel writer = new(new MemoryStream());
        await Assert.ThrowsAsync<ModuleProtocolException>(async () =>
            await writer.SendAsync(new InvokeMessage(1, "eco", Support.Json("\"" + new string('a', ProtocolVersion.MaxFrameBytes) + "\""))));
    }

    [Fact]
    public void Random_bytes_never_escape_as_anything_but_protocol_errors()
    {
        Random random = new(7);
        byte[] seed = Encoding.UTF8.GetBytes("""{"type":"invoke","id":1,"function":"eco","parameters":{"a":[1,"b"]},"buffers":[{"region":"r","offset":0,"length":4}]}""");
        for (int i = 0; i < 5000; i++)
        {
            byte[] data = (byte[])seed.Clone();
            for (int k = random.Next(1, 6); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
            try
            {
                MessageChannel.Parse(data);
            }
            catch (ModuleProtocolException)
            {
            }
        }
    }

    private static string Serialize(ModuleMessage message) =>
        System.Text.Json.JsonSerializer.Serialize(message, ProtocolJsonContext.Default.ModuleMessage);

    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
