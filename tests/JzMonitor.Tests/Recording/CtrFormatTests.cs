using JzMonitor.Protocol;
using JzMonitor.Recording;

namespace JzMonitor.Tests.Recording;

public class CtrFormatTests
{
    [Fact]
    public void SaveCtr_ThenLoadCtr_RoundTripsFramesAndFooter()
    {
        var rec = new LogRecording { Ctr = new CtrExtras() };
        rec.Vehicle.Values[0] = "Chaser";
        rec.Vehicle.Values[1] = "2JZ-GTE";
        rec.Comments.Add("Холодний пуск");
        rec.Ctr.Bookmarks[0] = "3";
        var t0 = new DateTime(2026, 11, 11, 10, 0, 0);
        for (int i = 0; i < 5; i++)
            rec.Frames.Add(new ObdFrame
            {
                Time = t0.AddSeconds(i),
                Bytes = Enumerable.Range(0, 12).Select(k => (byte)(i * 20 + k)).ToArray(),
                IdBits = [1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1],
                Marked = i == 2
            });
        var path = Path.Combine(Path.GetTempPath(), $"jz_{Guid.NewGuid():N}.ctr");

        try
        {
            CtrFormat.Save(path, rec);
            var back = CtrFormat.Load(path);

            Assert.Equal(5, back.Frames.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(rec.Frames[i].Bytes, back.Frames[i].Bytes);
                Assert.Equal(rec.Frames[i].IdBitString, back.Frames[i].IdBitString);
                Assert.Equal(rec.Frames[i].Marked, back.Frames[i].Marked);
                Assert.Equal(rec.Frames[i].Time.TimeOfDay, back.Frames[i].Time.TimeOfDay);
            }
            Assert.Equal("Chaser", back.Vehicle.Values[0]);
            Assert.Equal("2JZ-GTE", back.Vehicle.Values[1]);
            Assert.Equal(rec.Comments, back.Comments);
            Assert.Equal("3", back.Ctr!.Bookmarks[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveCtr_FrameLayout_Is40ValuesPlusTime()
    {
        var rec = new LogRecording();
        rec.Frames.Add(new ObdFrame { Time = new DateTime(2026, 1, 1, 9, 5, 7), Bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0b0011_0010, 0x80] });
        var path = Path.Combine(Path.GetTempPath(), $"jz_{Guid.NewGuid():N}.ctr");

        try
        {
            CtrFormat.Save(path, rec);
            var lines = File.ReadAllLines(path, RecordingIO.Cp1251);

            Assert.Equal(CtrFormat.Header, lines[0]);
            Assert.Equal("1", lines[1]);
            var frame = lines.Skip(2).Take(41).ToArray();
            Assert.Equal("10", frame[9]);
            Assert.Equal(["0", "1", "0", "0", "1", "1", "0", "0"], frame[23..31]); // байт 11, LSB першим
            Assert.Equal("1", frame[38]);                                          // 12.7
            Assert.Equal("9:05:07", frame[40]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Синтетичний файл у точному форматі, який пише CarTester 1.0.0.3 (структуру звірено з реальними
    /// записами: 75 порожніх рядків еталонів, 10 закладок, два Memo як «Count−1» + рядки, 9 полів авто).
    /// </summary>
    private static string WriteCarTesterStyleFile()
    {
        var lines = new List<string> { CtrFormat.Header, "2" };
        int[][] frames =
        [
            [242, 0, 125, 0, 95, 89, 0, 0, 0, 0],
            [40, 85, 88, 26, 36, 226, 0, 0, 128, 0],
        ];
        string[] times = ["13:39:29", "13:39:30"];
        for (int i = 0; i < frames.Length; i++)
        {
            lines.AddRange(frames[i].Select(v => v.ToString()));
            lines.Add("0");                                                        // службове значення
            lines.AddRange("100011100010".Select(c => c.ToString()));             // 12 біт ID
            lines.AddRange(["1", "1", "0", "0", "0", "1", "0", "0"]);              // байт 11
            lines.AddRange(["0", "1", "0", "1", "1", "0", "0", "1"]);              // байт 12
            lines.Add(i == 1 ? "1" : "0");                                         // мітка
            lines.Add(times[i]);
        }
        lines.AddRange(Enumerable.Repeat("", 75));
        lines.AddRange(["2", "0", "0", "0", "0", "0", "0", "0", "0", "0"]);
        lines.AddRange(["0", "тестовий запуск", "0", "друге memo"]);
        lines.AddRange(["Test car", "1JZ-GTE", "A340E", "1995", "100000", "Рядний", "6", "АКПП", "RWD"]);

        var path = Path.Combine(Path.GetTempPath(), $"jz_{Guid.NewGuid():N}.ctr");
        File.WriteAllText(path, string.Concat(lines.Select(l => l + "\r\n")), RecordingIO.Cp1251);
        return path;
    }

    [Fact]
    public void LoadCtr_CarTesterStyleFile_ReadsFramesAndFooter()
    {
        var path = WriteCarTesterStyleFile();

        try
        {
            var rec = CtrFormat.Load(path);

            Assert.Equal(2, rec.Frames.Count);
            Assert.Equal([40, 85, 88, 26, 36, 226, 0, 0, 128, 0, 0b0010_0011, 0b1001_1010], rec.Frames[1].Bytes);
            Assert.Equal("100011100010", rec.Frames[1].IdBitString);
            Assert.True(rec.Frames[1].Marked);
            Assert.Equal(new TimeSpan(13, 39, 30), rec.Frames[1].Time.TimeOfDay);
            Assert.Equal("2", rec.Ctr!.Bookmarks[0]);
            Assert.Equal(["тестовий запуск"], rec.Comments);
            Assert.Equal(["друге memo"], rec.Ctr.Comments2);
            Assert.Equal(["Test car", "1JZ-GTE", "A340E", "1995", "100000", "Рядний", "6", "АКПП", "RWD"], rec.Vehicle.Values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveCtr_CarTesterStyleFile_IsByteIdenticalAfterLoad()
    {
        var path = WriteCarTesterStyleFile();
        var copy = Path.Combine(Path.GetTempPath(), $"jz_{Guid.NewGuid():N}.ctr");

        try
        {
            CtrFormat.Save(copy, CtrFormat.Load(path));

            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(path);
            File.Delete(copy);
        }
    }
}
