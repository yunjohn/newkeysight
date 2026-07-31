namespace KeysightScopeApp.Core.Waveforms;

public enum WaveformInteractionTool
{
    Pan,
    ZoomBox,
    CursorA,
    CursorB,
    VoltageCursorA,
    VoltageCursorB,
    ChannelMove,
    Annotate,
    Inspect
}

public sealed record WaveformViewState(
    TimeRange XRange,
    TimeRange YRange,
    HashSet<string> VisibleChannels,
    Dictionary<string, double> ChannelOffsets,
    double? CursorA = null,
    double? CursorB = null);

public sealed record WaveformWorkspace(
    int SchemaVersion,
    WaveformViewState View,
    Dictionary<string, WaveformViewState> Bookmarks,
    List<WaveformAnnotation> Annotations,
    WaveformWindowPlacement? Window = null)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record WaveformWindowPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    bool Maximized = false);

public sealed class WaveformViewHistory(int capacity = 100)
{
    private readonly List<WaveformViewState> states = [];
    private int position = -1;

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    public bool CanUndo => position > 0;
    public bool CanRedo => position >= 0 && position < states.Count - 1;
    public WaveformViewState? Current => position >= 0 ? states[position] : null;

    public void Push(WaveformViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Current is not null && Equivalent(Current, state)) return;
        if (position < states.Count - 1)
            states.RemoveRange(position + 1, states.Count - position - 1);
        states.Add(state);
        if (states.Count > Capacity)
            states.RemoveAt(0);
        position = states.Count - 1;
    }

    public WaveformViewState? Undo()
    {
        if (!CanUndo) return null;
        return states[--position];
    }

    public WaveformViewState? Redo()
    {
        if (!CanRedo) return null;
        return states[++position];
    }

    public void Clear()
    {
        states.Clear();
        position = -1;
    }

    private static bool Equivalent(WaveformViewState left, WaveformViewState right) =>
        left.XRange == right.XRange &&
        left.YRange == right.YRange &&
        left.CursorA == right.CursorA &&
        left.CursorB == right.CursorB &&
        left.VisibleChannels.SetEquals(right.VisibleChannels) &&
        left.ChannelOffsets.Count == right.ChannelOffsets.Count &&
        left.ChannelOffsets.All(item =>
            right.ChannelOffsets.TryGetValue(item.Key, out double value) && value == item.Value);
}

public sealed record WaveformAnnotation(
    Guid Id,
    string Text,
    string? Channel,
    double TimeSeconds,
    double Value,
    DateTimeOffset CreatedAt)
{
    public static WaveformAnnotation Create(string text, string? channel, double timeSeconds, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(value))
            throw new ArgumentException("标注坐标必须是有限数值。");
        return new(Guid.NewGuid(), text.Trim(), channel, timeSeconds, value, DateTimeOffset.UtcNow);
    }
}
