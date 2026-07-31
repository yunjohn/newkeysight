using KeysightScopeApp.Core.Waveforms;

namespace KeysightScopeApp.Core.Tests;

public sealed class WaveformViewStateTests
{
    private static WaveformViewState State(double left) =>
        new(new(left, left + 1), new(-1, 1), new HashSet<string> { "CHANnel1" },
            new Dictionary<string, double>(), left, left + .5);

    [Fact]
    public void UndoRedoAndBranchingAreDeterministic()
    {
        var history = new WaveformViewHistory();
        history.Push(State(0));
        history.Push(State(1));
        history.Push(State(2));

        Assert.Equal(1, history.Undo()!.XRange.Start);
        Assert.Equal(0, history.Undo()!.XRange.Start);
        Assert.Equal(1, history.Redo()!.XRange.Start);

        history.Push(State(9));

        Assert.False(history.CanRedo);
        Assert.Equal(9, history.Current!.XRange.Start);
    }

    [Fact]
    public void CapacityDropsOldestState()
    {
        var history = new WaveformViewHistory(2);
        history.Push(State(0));
        history.Push(State(1));
        history.Push(State(2));

        Assert.Equal(1, history.Undo()!.XRange.Start);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AnnotationValidatesCoordinatesAndText()
    {
        WaveformAnnotation annotation = WaveformAnnotation.Create("  事件  ", "CHANnel2", 1.5, 3);
        Assert.Equal("事件", annotation.Text);
        Assert.Throws<ArgumentException>(() => WaveformAnnotation.Create("", null, 0, 0));
        Assert.Throws<ArgumentException>(() => WaveformAnnotation.Create("x", null, double.NaN, 0));
    }
}
