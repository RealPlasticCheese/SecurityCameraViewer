namespace SecurityCameraViewer.Core;

/// <summary>G.711 companding of signed 16-bit voice samples, without an external encoder process.</summary>
public static class G711
{
    public static byte Encode(short sample, bool aLaw)
    {
        var value = (int)sample;
        if (aLaw)
        {
            value >>= 3;
            var mask = value >= 0 ? 0xd5 : 0x55;
            if (value < 0)
            {
                value = -value - 1;
            }
            var segment = 0;
            for (var boundary = 0x1f; segment < 8 && value > boundary; boundary = (boundary << 1) | 1)
            {
                segment++;
            }
            var encoded = segment >= 8 ? 0x7f : segment << 4 | (value >> (segment < 2 ? 1 : segment) & 0x0f);
            return (byte)(encoded ^ mask);
        }
        var sign = value < 0 ? 0x80 : 0;
        value = Math.Min(value < 0 ? -value : value, 32635) + 0x84;
        var exponent = 7;
        for (var boundary = 0x4000; exponent > 0 && (value & boundary) == 0; boundary >>= 1)
        {
            exponent--;
        }
        return (byte)~(sign | exponent << 4 | value >> (exponent + 3) & 0x0f);
    }
}
