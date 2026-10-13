using System.Numerics;

namespace Musync.Core;

internal static class AcousticCorrelation
{
    internal readonly record struct Match(int Sample, double Score, double Alternative);
    public static Match Find(ReadOnlySpan<float> samples, float[] marker, int start, int end, CancellationToken token)
    {
        start = Math.Max(0, start); end = Math.Min(end, samples.Length - marker.Length + 1);
        if (end <= start) return new(-1, 0, 0);
        var length = end - start + marker.Length - 1;
        var size = 1;
        while (size < length + marker.Length - 1) size <<= 1;
        var audio = new Complex[size]; var kernel = new Complex[size];
        var sums = new double[length + 1]; var powers = new double[length + 1];
        var mean = marker.Average(v => (double)v); double markerPower = 0;
        for (var i = 0; i < length; i++)
        {
            var value = samples[start + i]; audio[i] = value;
            sums[i + 1] = sums[i] + value; powers[i + 1] = powers[i] + value * (double)value;
        }
        for (var i = 0; i < marker.Length; i++) { var value = marker[i] - mean; kernel[i] = value; markerPower += value * value; }
        Transform(audio, false, token); Transform(kernel, false, token);
        for (var i = 0; i < size; i++) audio[i] *= Complex.Conjugate(kernel[i]);
        Transform(audio, true, token);
        var scores = new double[end - start]; var strengths = new double[scores.Length];
        double best = 0, bestStrength = 0, maximumScore = 0; var peak = -1;
        for (var i = 0; i < scores.Length; i++)
        {
            var sum = sums[i + marker.Length] - sums[i];
            var power = powers[i + marker.Length] - powers[i] - sum * sum / marker.Length;
            var score = power > 1e-12 ? Math.Clamp(Math.Abs(audio[i].Real) / Math.Sqrt(power * markerPower), 0, 1) : 0;
            scores[i] = score;
            strengths[i] = Math.Abs(audio[i].Real) / Math.Sqrt(markerPower);
            maximumScore = Math.Max(maximumScore, score);
            // A quiet isolated echo can have a higher normalized correlation
            // than the louder arrival mixed with the reference. Select by
            // matched signal strength among candidates with adequate quality.
            if (score >= .45 && strengths[i] > bestStrength) { best = score; bestStrength = strengths[i]; peak = i; }
        }
        double alternative = 0;
        for (var i = 0; i < scores.Length; i++)
            if (scores[i] >= .45 && Math.Abs(i - peak) >= marker.Length) alternative = Math.Max(alternative, strengths[i]);
        token.ThrowIfCancellationRequested();
        return peak < 0 ? new(-1, maximumScore, 0) : new(start + peak, best, best * alternative / bestStrength);
    }
    private static void Transform(Complex[] values, bool inverse, CancellationToken token)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            token.ThrowIfCancellationRequested();
            var step = Complex.FromPolarCoordinates(1, (inverse ? Math.Tau : -Math.Tau) / length);
            for (var first = 0; first < n; first += length)
            {
                var rotation = Complex.One;
                for (var i = 0; i < length / 2; i++)
                {
                    var even = values[first + i]; var odd = rotation * values[first + i + length / 2];
                    values[first + i] = even + odd; values[first + i + length / 2] = even - odd; rotation *= step;
                }
            }
        }
        if (inverse) for (var i = 0; i < n; i++) values[i] /= n;
    }
}
