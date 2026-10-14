using System.Buffers.Binary;

namespace SupportDesk.Application.Ai.Retrieval;

/// <summary>Embedding vector helpers. Stored format: float32 little-endian, independent of the host's byte order.</summary>
public static class VectorMath
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), vector[i]);
        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 4 != 0) throw new ArgumentException("Embedding byte length must be a multiple of 4.", nameof(bytes));
        var vector = new float[bytes.Length / 4];
        for (var i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
        return vector;
    }

    /// <summary>In-place L2 normalisation; a zero vector stays zero.</summary>
    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += (double)v * v;
        if (sum <= 0) return vector;
        var inv = (float)(1.0 / Math.Sqrt(sum));
        for (var i = 0; i < vector.Length; i++) vector[i] *= inv;
        return vector;
    }

    /// <summary>Cosine similarity in [-1, 1] (0 for empty or mismatched vectors).</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Dot product of two L2-normalised vectors (= cosine).</summary>
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }
}
