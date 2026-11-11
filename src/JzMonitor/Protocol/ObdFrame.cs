namespace JzMonitor.Protocol;

/// <summary>
/// Один кадр потоку даних Toyota OBD-1.
/// Структура (з дизасемблювання CarTester):
///   заголовок 15 біт: старт 0, 12 біт ID, 2 стоп-біти 1;
///   далі N слів по 11 біт: старт 0, 8 біт даних (LSB першим), 2 стоп-біти 1.
/// Оригінал приймав рівно 12 байтів, решту відкидав. Тут зберігаються всі.
/// </summary>
public sealed class ObdFrame
{
    public DateTime Time { get; init; }
    /// <summary>12 біт заголовка в порядку прийому.</summary>
    public byte[] IdBits { get; init; } = new byte[12];
    /// <summary>Байти даних; Bytes[0] — це «байт 1» у нумерації CarTester.</summary>
    public byte[] Bytes { get; init; } = [];
    public bool Valid { get; init; } = true;
    public string? Error { get; init; }
    /// <summary>Позначка «Something» з .ctr (ручна мітка кадру в оригіналі).</summary>
    public bool Marked { get; set; }

    public string IdBitString => string.Concat(IdBits.Select(b => b != 0 ? '1' : '0'));
    public int IdValue
    {
        get
        {
            int v = 0;
            for (int i = 0; i < IdBits.Length; i++) if (IdBits[i] != 0) v |= 1 << i;
            return v;
        }
    }

    /// <summary>Значення байта за номером CarTester (1..N); null, якщо такого байта немає.</summary>
    public byte? B(int number) => number >= 1 && number <= Bytes.Length ? Bytes[number - 1] : null;

    public bool Bit(int byteNumber, int bit) => B(byteNumber) is byte v && ((v >> bit) & 1) != 0;

    public string HexDump => string.Join(" ", Bytes.Select(b => b.ToString("X2")));
}
