using System.Buffers.Binary;

namespace PiUsbAudio.Control;

public sealed record StreamDeckKeyDisplay(int Button, string Name, string State);

// A small bitmap font keeps the appliance independent of desktop fonts,
// graphics libraries, Python, and System.Drawing's Windows-only implementation.
public static class StreamDeckImages
{
    public const int Size = 80;
    public const int BitmapSize = 54 + Size * Size * 3;
    private const string Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 /-._?";
    private static readonly string[] Glyphs =
    [
        "0E11111F111111", "1E11111E11111E", "0E11101010110E", "1E11111111111E",
        "1F10101E10101F", "1F10101E101010", "0E11101711110F", "1111111F111111",
        "0E04040404040E", "0702020212120C", "11121418141211", "1010101010101F",
        "111B1515111111", "11191513111111", "0E11111111110E", "1E11111E101010",
        "0E11111115120D", "1E11111E141211", "0F10100E01011E", "1F040404040404",
        "1111111111110E", "11111111110A04", "11111115151B11", "11110A040A1111",
        "11110A04040404", "1F01020408101F", "0E11131519110E", "040C040404040E",
        "0E11010204081F", "1E01010601011E", "02060A121F0202", "1F101E0101110E",
        "0608101E11110E", "1F010204080808", "0E11110E11110E", "0E11110F01020C",
        "00000000000000", "01010204081010", "0000001F000000", "00000000000C0C",
        "0000000000001F", "0E110102040004"
    ];

    public static byte[] Render(StreamDeckKeyDisplay display)
    {
        var background = display.State switch
        {
            "LIVE" => 0x146434,
            "MUTED" or "MUTE ALL" => 0x9d202b,
            "ERROR" or "OFFLINE" => 0x805100,
            _ => 0x202630
        };
        var pixels = new byte[Size * Size * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)(background >> 16);
            pixels[i + 1] = (byte)(background >> 8);
            pixels[i + 2] = (byte)background;
        }
        if (display.Button == 6)
        {
            Text(pixels, "PANIC", 7, 2);
            if (display.State == "MUTE ALL")
            {
                Text(pixels, "MUTE", 33, 2);
                Text(pixels, "ALL", 55, 2);
            }
            else
                Text(pixels, display.State, 55, display.State.Length <= 6 ? 2 : 1);
            return EncodeBitmap(pixels);
        }
        Text(pixels, display.Button <= 4 ? $"MIC {display.Button}" : $"KEY {display.Button}", 7, 2);
        var name = display.Name.ToUpperInvariant();
        var lines = Wrap(name, 12).Take(2).ToArray();
        for (var i = 0; i < lines.Length; i++)
            Text(pixels, lines[i], 30 + i * 10, 1);
        Text(pixels, display.State, 59, display.State.Length <= 6 ? 2 : 1);
        return EncodeBitmap(pixels);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        while (text.Length > width)
        {
            var split = text.LastIndexOf(' ', width, width);
            if (split <= 0)
                split = width;
            yield return text[..split];
            text = text[split..].TrimStart();
        }
        yield return text;
    }

    private static void Text(byte[] pixels, string text, int top, int scale)
    {
        var left = (Size - (text.Length * 6 - 1) * scale) / 2;
        foreach (var character in text)
        {
            var index = Characters.IndexOf(character);
            var glyph = Convert.FromHexString(Glyphs[index < 0 ? Characters.IndexOf('?') : index]);
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 5; x++)
                    if ((glyph[y] & (1 << (4 - x))) != 0)
                        for (var dy = 0; dy < scale; dy++)
                            for (var dx = 0; dx < scale; dx++)
                            {
                                var px = left + x * scale + dx;
                                var py = top + y * scale + dy;
                                if (px is >= 0 and < Size && py is >= 0 and < Size)
                                    pixels.AsSpan((py * Size + px) * 3, 3).Fill(255);
                            }
            left += 6 * scale;
        }
    }

    public static byte[] EncodeBitmap(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != Size * Size * 3)
            throw new ArgumentException("Expected an 80 by 80 RGB image.", nameof(rgb));
        var bitmap = new byte[BitmapSize];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), BitmapSize);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), Size);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), Size);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(34), Size * Size * 3);
        // Mini orientation: rotate 90 degrees counterclockwise, then flip Y,
        // as in python-elgato-streamdeck's Mini image helper. BMP rows are BGR
        // and bottom-up. 80 * 3 bytes is already aligned to four bytes.
        for (var row = 0; row < Size; row++)
            for (var x = 0; x < Size; x++)
            {
                var source = (x * Size + Size - 1 - row) * 3;
                var target = 54 + (row * Size + x) * 3;
                bitmap[target] = rgb[source + 2];
                bitmap[target + 1] = rgb[source + 1];
                bitmap[target + 2] = rgb[source];
            }
        return bitmap;
    }
}
