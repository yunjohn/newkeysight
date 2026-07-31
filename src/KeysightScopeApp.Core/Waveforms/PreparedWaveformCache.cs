namespace KeysightScopeApp.Core.Waveforms;

public sealed class PreparedWaveformCache(int capacity = 64)
{
    private sealed record CacheKey(
        WaveformData Waveform,
        double Start,
        double End,
        int PixelWidth,
        long DataVersion);

    private sealed record CacheEntry(
        PreparedWaveformDisplay Value,
        LinkedListNode<CacheKey> Node);

    private readonly object gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> entries = [];
    private readonly LinkedList<CacheKey> leastRecentlyUsed = [];

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    public PreparedWaveformDisplay GetOrPrepare(
        WaveformData waveform,
        TimeRange range,
        int pixelWidth,
        long dataVersion,
        CancellationToken token = default)
    {
        var key = new CacheKey(waveform, range.Minimum, range.Maximum, pixelWidth, dataVersion);
        lock (gate)
        {
            if (entries.TryGetValue(key, out CacheEntry? cached))
            {
                leastRecentlyUsed.Remove(cached.Node);
                leastRecentlyUsed.AddFirst(cached.Node);
                return cached.Value;
            }
        }

        PreparedWaveformDisplay prepared =
            EnvelopeDecimator.Prepare(waveform, range, pixelWidth, token);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (entries.TryGetValue(key, out CacheEntry? raced))
            {
                leastRecentlyUsed.Remove(raced.Node);
                leastRecentlyUsed.AddFirst(raced.Node);
                return raced.Value;
            }
            LinkedListNode<CacheKey> node = leastRecentlyUsed.AddFirst(key);
            entries[key] = new(prepared, node);
            while (entries.Count > Capacity)
            {
                LinkedListNode<CacheKey> oldest = leastRecentlyUsed.Last!;
                leastRecentlyUsed.RemoveLast();
                entries.Remove(oldest.Value);
            }
        }
        return prepared;
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            leastRecentlyUsed.Clear();
        }
    }
}
