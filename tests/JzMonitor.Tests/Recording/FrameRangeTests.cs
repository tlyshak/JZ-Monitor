using JzMonitor.Protocol;
using JzMonitor.Recording;

namespace JzMonitor.Tests.Recording;

public class FrameRangeTests
{
    /// <summary>10 кадрів щосекунди, починаючи з 23:59:55 (запис переходить через північ).</summary>
    private static List<ObdFrame> Frames(int count = 10, int stepSeconds = 1)
    {
        var t0 = new DateTime(2026, 11, 9, 23, 59, 55);
        return Enumerable.Range(0, count).Select(i => new ObdFrame { Time = t0.AddSeconds(i * stepSeconds), Bytes = new byte[12] }).ToList();
    }

    [Fact]
    public void TryParse_Frames_OneBasedInclusive()
    {
        Assert.True(RangeParser.TryParse(Frames(), RangeMode.Frames, "3", "7", out var r, out _));
        Assert.Equal(new FrameRange(2, 6), r);
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void TryParse_Frames_EmptyMeansStartOrEnd()
    {
        Assert.True(RangeParser.TryParse(Frames(), RangeMode.Frames, "", " ", out var r, out _));
        Assert.Equal(new FrameRange(0, 9), r);
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("3", "11")]
    [InlineData("8", "2")]
    [InlineData("abc", "5")]
    public void TryParse_Frames_Invalid(string from, string to)
    {
        Assert.False(RangeParser.TryParse(Frames(), RangeMode.Frames, from, to, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_Time_AcrossMidnight_ToIncludesWholeSecond()
    {
        // 23:59:58 → кадр 3 (індекс 3); 00:00:02 → кадр о 00:00:02 (індекс 7)
        Assert.True(RangeParser.TryParse(Frames(), RangeMode.Time, "23:59:58", "00:00:02", out var r, out var e), e);
        Assert.Equal(new FrameRange(3, 7), r);
    }

    [Fact]
    public void TryParse_Time_OutsideRecording_Fails()
    {
        Assert.False(RangeParser.TryParse(Frames(), RangeMode.Time, "10:00", "11:00", out _, out var error));
        Assert.Contains("23:59:55", error);
    }

    [Fact]
    public void Format_FramesAndTime()
    {
        var f = Frames();
        Assert.Equal("4", RangeParser.Format(f, RangeMode.Frames, 3));
        Assert.Equal("23:59:58", RangeParser.Format(f, RangeMode.Time, 3));
    }

    [Fact]
    public void Timeline_BySecond_HoldsLastFrame_AndEndsWithRange()
    {
        var frames = Frames(count: 5, stepSeconds: 2);   // кадри на 0, 2, 4, 6, 8 с
        var map = Timeline.BySecond(frames, new FrameRange(1, 3), seconds: 8); // діапазон 2…6 с → 0…4 с

        Assert.Equal([1, 1, 2, 2, 3, -1, -1, -1], map);
    }
}
