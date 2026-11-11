namespace JzMonitor.Tests.Protocol;

public class FrameAssemblerTests
{
    [Fact]
    public void Push_RandomFramesWithJitter_DecodesAllBytesAndIds()
    {
        var rnd = new Random(42);
        var src = Enumerable.Range(0, 200)
            .Select(_ => (rnd.Next(4096), Enumerable.Range(0, 12).Select(_ => (byte)rnd.Next(256)).ToArray()))
            .ToList();

        var got = SignalBuilder.Decode(src, jitterMs: 2.0);

        Assert.Equal(200, got.Count);
        Assert.All(got, f => Assert.True(f.Valid, f.Error));
        for (int i = 0; i < src.Count; i++)
        {
            Assert.Equal(src[i].Item2, got[i].Bytes);
            Assert.Equal(src[i].Item1, got[i].IdValue);
        }
    }

    [Fact]
    public void Push_LastByteEndsWithOnes_IsNotLostInGap()
    {
        var data = new byte[12];
        data[11] = 0xFF;

        var got = SignalBuilder.Decode([(0xFFF, data), (0, new byte[12])], jitterMs: 1.0);

        Assert.Equal(2, got.Count);
        Assert.True(got[0].Valid, got[0].Error);
        Assert.Equal(0xFF, got[0].Bytes[11]);
        Assert.Equal(0xFFF, got[0].IdValue);
        Assert.All(got[1].Bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Push_FrameLongerThan12Bytes_KeepsAllBytes()
    {
        var data = Enumerable.Range(1, 16).Select(i => (byte)(i * 13)).ToArray();

        var got = SignalBuilder.Decode([(0x123, data)], jitterMs: 1.0);

        var frame = Assert.Single(got);
        Assert.True(frame.Valid, frame.Error);
        Assert.Equal(data, frame.Bytes);
    }

    [Fact]
    public void Push_FrameShorterThanMinimum_IsMarkedInvalid()
    {
        var got = SignalBuilder.Decode([(5, new byte[5])], jitterMs: 0.5);

        var frame = Assert.Single(got);
        Assert.False(frame.Valid);
        Assert.NotNull(frame.Error);
    }
}
