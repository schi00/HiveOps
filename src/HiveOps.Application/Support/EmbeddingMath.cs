namespace HiveOps.Application.Support;

public static class EmbeddingMath
{
    public static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || b.Count == 0 || a.Count != b.Count)
            return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom < 1e-12 ? 0 : dot / denom;
    }

    public static float[] FloatsFromLittleEndianBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % 4 != 0)
            return [];

        var n = bytes.Length / 4;
        var v = new float[n];
        for (var i = 0; i < n; i++)
            v[i] = BitConverter.ToSingle(bytes.Slice(i * 4, 4));

        return v;
    }

    public static byte[] FloatsToLittleEndianBytes(IReadOnlyList<float> values)
    {
        var buf = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
            BitConverter.TryWriteBytes(buf.AsSpan(i * 4), values[i]);

        return buf;
    }
}
