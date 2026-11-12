namespace SecurityCameraViewer.Nvr.Raysharp;

/// <summary>RaySharp's wire permutation; this is obfuscation, not transport security.</summary>
internal static class YsfCipher
{
    // Ported from pyswann 0.0.29 (MIT); see THIRD-PARTY-NOTICES.md.
    // A circular linked index avoids the reference implementation's quadratic list removals.
    public static byte[] Transform(ReadOnlySpan<byte> source, bool decrypt)
    {
        if (source.IsEmpty)
        {
            return [];
        }

        var bitCount = checked(source.Length * 8);
        var next = new int[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            next[i] = (i + 1) % bitCount;
        }

        var output = new byte[source.Length];
        var cursor = 1;
        for (var i = 0; i < bitCount; i++)
        {
            var remaining = bitCount - i;
            for (var step = 0; step < 2 % remaining; step++)
            {
                cursor = next[cursor];
            }

            var selected = next[cursor];
            next[cursor] = next[selected];
            var from = decrypt ? i : selected;
            var to = decrypt ? selected : i;
            output[to / 8] |= (byte)(((source[from / 8] >> (7 - from % 8)) & 1) << (7 - to % 8));
        }

        return output;
    }
}
