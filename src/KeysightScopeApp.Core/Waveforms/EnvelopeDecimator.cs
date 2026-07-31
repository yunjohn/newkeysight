namespace KeysightScopeApp.Core.Waveforms;

public static class EnvelopeDecimator
{
    public static PreparedWaveformDisplay Prepare(
        WaveformData waveform,
        TimeRange range,
        int pixelWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        (int start, int end) = WaveformAnalysis.LocateRange(waveform.X, range);
        int count = end - start + 1;
        int bucketCount = Math.Max(1, pixelWidth);
        if (count <= bucketCount * 2)
            return new(waveform.Channel, waveform.X[start..(end + 1)], waveform.Y[start..(end + 1)], range, count);

        var indices = new List<int>(bucketCount * 2 + 2) { start };
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int left = start + (int)((long)bucket * count / bucketCount);
            int right = start + (int)((long)(bucket + 1) * count / bucketCount);
            right = Math.Min(right, end + 1);
            if (left >= right) continue;
            int minIndex = left, maxIndex = left;
            for (int i = left + 1; i < right; i++)
            {
                if (waveform.Y[i] < waveform.Y[minIndex]) minIndex = i;
                if (waveform.Y[i] > waveform.Y[maxIndex]) maxIndex = i;
            }
            if (minIndex < maxIndex) { indices.Add(minIndex); indices.Add(maxIndex); }
            else if (maxIndex < minIndex) { indices.Add(maxIndex); indices.Add(minIndex); }
            else indices.Add(minIndex);
        }
        indices.Add(end);
        int[] unique = [.. indices.Distinct().Order()];
        return new(
            waveform.Channel,
            unique.Select(index => waveform.X[index]).ToArray(),
            unique.Select(index => waveform.Y[index]).ToArray(),
            range,
            count);
    }
}
