using KeysightScopeApp.Core.Instruments;
using KeysightScopeApp.Core.Waveforms;
using KeysightScopeApp.Infrastructure.Instruments;

namespace KeysightScopeApp.Infrastructure.Tests;

public sealed class KeysightOscilloscopeTests
{
    [Fact]
    public async Task DeviceStatusUsesFastTriggerEventQuery()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":TIMebase:MODE?"] = "MAIN";
        transport.Queries[":ACQuire:TYPE?"] = "NORM";
        transport.Queries[":TER?"] = "0";
        var scope = new KeysightOscilloscope(transport);

        (ScopeOperatingSettings operating, string triggerStatus) =
            await scope.GetDeviceStatusWithRecoveryAsync();

        Assert.Equal("MAIN", operating.TimebaseMode);
        Assert.Equal("NORM", operating.AcquireType);
        Assert.Equal("WAIT", triggerStatus);
        Assert.Contains(("query", ":TER?"), transport.Commands);
        Assert.DoesNotContain(("query", ":TRIGger:STATus?"), transport.Commands);
    }

    [Theory]
    [InlineData("+1", true)]
    [InlineData("+0", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task TriggerEventAcceptsSignedAndUnsignedIntegerResponses(
        string response,
        bool expected)
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":TER?"] = response;
        var scope = new KeysightOscilloscope(transport);

        Assert.Equal(expected, await scope.GetTriggerEventAsync(1200));
    }

    [Fact]
    public async Task CapturesAndScalesByteWaveform()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries["*IDN?"] = "KEYSIGHT,DSOX1204G,MY123,1.0";
        transport.Queries[":WAVeform:POINts?"] = "3";
        transport.Queries[":WAVeform:PREamble?"] = "0,0,3,1,0.5,1,0,0.1,0,0";
        transport.BinaryQueries[":WAVeform:DATA?"] = [0, 10, 20];
        var scope = new KeysightOscilloscope(transport);

        InstrumentIdentity identity = await scope.IdentifyAsync();
        CaptureResult result = await scope.CaptureAsync(new(["CHANnel1"], "RAW", 3));

        Assert.Equal("DSOX1204G", identity.Model);
        Assert.Equal([1, 1.5, 2], result.Bundle["CHANnel1"].X);
        Assert.Equal([0, 1, 2], result.Bundle["CHANnel1"].Y);
        Assert.Contains(("write", ":ACQuire:TYPE NORMal"), transport.Commands);
        Assert.Contains(("write", ":WAVeform:UNSigned ON"), transport.Commands);
        Assert.Contains(("clear", ""), transport.Commands);
    }

    [Fact]
    public async Task CaptureReadsChannelAndProbeMetadata()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":CHANnel1:UNITs?"] = "AMP";
        transport.Queries[":CHANnel1:PROBe?"] = "10";
        transport.Queries[":CHANnel1:PROBe:ID?"] = "N2843A";
        transport.Queries[":CHANnel1:PROBe:HEAD:TYPE?"] = "PASSIVE";
        transport.Queries[":CHANnel1:SCALe?"] = "0.5";
        transport.Queries[":CHANnel1:OFFSet?"] = "-0.25";
        transport.Queries[":CHANnel1:COUPling?"] = "DC";
        transport.Queries[":CHANnel1:IMPedance?"] = "ONEMeg";
        transport.Queries[":CHANnel1:BWLimit?"] = "1";
        transport.Queries[":CHANnel1:INVert?"] = "0";
        transport.Queries[":CHANnel1:DISPlay?"] = "1";
        transport.Queries[":CHANnel1:LABel?"] = "Motor current";
        transport.Queries[":WAVeform:PREamble?"] = "0,0,3,1,0.5,1,0,0.1,0,0";
        transport.BinaryQueries[":WAVeform:DATA?"] = [0, 10, 20];
        var scope = new KeysightOscilloscope(transport);

        WaveformData waveform = await scope.FetchWaveformAsync("CHANnel1", "RAW", 3);

        Assert.Equal("AMP", waveform.Unit);
        Assert.Equal(10, waveform.Acquisition!.ProbeAttenuation);
        Assert.Equal("N2843A", waveform.Acquisition.ProbeId);
        Assert.Equal(.5, waveform.Acquisition.VerticalScale);
        Assert.True(waveform.Acquisition.Displayed);
        Assert.Equal("Motor current", waveform.Acquisition.Label);
    }

    [Fact]
    public async Task SameSessionCanCaptureWaveformTwice()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":WAVeform:PREamble?"] = "0,0,3,1,0.5,1,0,0.1,0,0";
        transport.BinaryQueries[":WAVeform:DATA?"] = [0, 10, 20];
        var scope = new KeysightOscilloscope(transport);
        var request = new CaptureRequest(["CHANnel1"], "NORMal", 3);

        CaptureResult first = await scope.CaptureAsync(request);
        CaptureResult second = await scope.CaptureAsync(request);

        Assert.Equal(first.Bundle["CHANnel1"].Y, second.Bundle["CHANnel1"].Y);
        Assert.Equal(2, transport.Commands.Count(item => item == ("clear", "")));
        Assert.Equal(2, transport.Commands.Count(
            item => item == ("query", ":WAVeform:PREamble?")));
    }

    [Fact]
    public async Task TruncatedPayloadIsRejected()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":WAVeform:PREamble?"] = "0,0,3,1,1,0,0,1,0,0";
        transport.BinaryQueries[":WAVeform:DATA?"] = [1, 2];
        var scope = new KeysightOscilloscope(transport);

        await Assert.ThrowsAsync<WaveformIntegrityException>(
            () => scope.FetchWaveformAsync("CHANnel1", "RAW", 3));
    }

    [Fact]
    public async Task ChunkedReadRequestsContiguousRangesAndReportsProgress()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":WAVeform:PREamble?"] = "0,0,7,1,0.25,1,0,0.5,0,0";
        transport.BinaryQuerySequences[":WAVeform:DATA?"] = new(
            new byte[][] { [0, 1, 2], [3, 4, 5], [6] });
        var progress = new List<double>();
        var scope = new KeysightOscilloscope(transport);

        WaveformData waveform = await scope.FetchWaveformChunkedAsync(
            "CHANnel1", chunkPoints: 3, totalPoints: 7,
            progress: new Progress<double>(progress.Add));

        Assert.Equal(7, waveform.Count);
        Assert.Equal(2.5, waveform.X[^1]);
        Assert.Equal(3, waveform.Y[^1]);
        Assert.Contains(("write", ":WAVeform:STARt 1"), transport.Commands);
        Assert.Contains(("write", ":WAVeform:STOP 7"), transport.Commands);
        Assert.Equal(3, transport.Commands.Count(item => item == ("query_binary", ":WAVeform:DATA?")));
    }

    [Fact]
    public async Task CaptureAutomaticallyChunksLargeRawRecords()
    {
        int points = KeysightOscilloscope.ChunkedReadThreshold + 1;
        var transport = new ScriptedScopeTransport();
        transport.Queries[":WAVeform:POINts?"] = points.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        transport.Queries[":WAVeform:PREamble?"] =
            $"0,0,{points},1,0.001,0,0,1,0,0";
        transport.BinaryQuerySequences[":WAVeform:DATA?"] = new(
            new byte[][]
            {
                new byte[KeysightOscilloscope.ChunkedReadThreshold],
                [1]
            });
        var scope = new KeysightOscilloscope(transport);

        CaptureResult result = await scope.CaptureAsync(
            new(["CHANnel1"], "RAW", points));

        Assert.Equal(points, result.Bundle["CHANnel1"].Count);
        Assert.Equal(2, transport.Commands.Count(
            item => item == ("query_binary", ":WAVeform:DATA?")));
        Assert.Contains(("write", $":WAVeform:STARt {KeysightOscilloscope.ChunkedReadThreshold + 1}"),
            transport.Commands);
    }

    [Fact]
    public async Task FullDeepMemoryAutoDetectsAndChunksInCurrentPointsMode()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":WAVeform:POINts?"] = "3";
        transport.Queries[":WAVeform:PREamble?"] = "0,0,3,1,0.5,1,0,0.1,0,0";
        transport.BinaryQuerySequences[":WAVeform:DATA?"] =
            new(new byte[][] { [0, 10, 20] });
        var scope = new KeysightOscilloscope(transport);

        CaptureResult result = await scope.CaptureAsync(
            new(["CHANnel1"], "NORMal", 20_000, FullDeepMemory: true));

        Assert.Equal(3, result.Bundle["CHANnel1"].Count);
        Assert.Contains(("write", ":WAVeform:POINts:MODE NORMal"), transport.Commands);
        Assert.Contains(("write", ":WAVeform:POINts 100000000"), transport.Commands);
        Assert.Contains(("query", ":WAVeform:POINts?"), transport.Commands);
    }

    [Fact]
    public async Task ReadsAndWritesVerticalAndOperatingSettings()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":CHANnel2:SCALe?"] = ".5";
        transport.Queries[":CHANnel2:OFFSet?"] = "-1.25";
        transport.Queries[":CHANnel2:DISPlay?"] = "ON";
        transport.Queries[":TIMebase:MODE?"] = "MAIN";
        transport.Queries[":ACQuire:TYPE?"] = "HRES";
        var scope = new KeysightOscilloscope(transport);

        ChannelVerticalSettings vertical = await scope.GetChannelVerticalAsync("CHANnel2");
        ScopeOperatingSettings operating = await scope.GetOperatingSettingsAsync();
        await scope.SetChannelVerticalAsync("CHANnel2", .25, -2);
        await scope.SetOperatingSettingsAsync(new("ROLL", "AVERage"));

        Assert.Equal(new(.5, -1.25, true), vertical);
        Assert.Equal("MAIN", operating.TimebaseMode);
        Assert.Contains(("write", ":CHANnel2:SCALe 0.25"), transport.Commands);
        Assert.Contains(("write", ":TIMebase:MODE ROLL"), transport.Commands);
        Assert.Contains(("write", ":ACQuire:TYPE AVERage"), transport.Commands);
    }

    [Fact]
    public async Task SingleWaitTemporarilyLeavesRollModeAndRestoresItWhenTriggered()
    {
        var transport = new ScriptedScopeTransport();
        transport.QuerySequences[":TER?"] = new(["0", "1"]);
        var scope = new KeysightOscilloscope(transport);

        string status = await scope.SingleAndWaitAsync(
            new("CHANnel1", "POSitive", 2.5, "NORMal"),
            new("ROLL", "HRESolution"),
            TimeSpan.FromSeconds(1));

        Assert.Equal("TRIGGERED", status);
        Assert.Contains(("write", ":TIMebase:MODE MAIN"), transport.Commands);
        Assert.Contains(("write", ":SINGle"), transport.Commands);
        Assert.Equal(("write", ":TIMebase:MODE ROLL"), transport.Commands.Last());
    }

    [Fact]
    public async Task SingleWaitRestoresRollModeWhenCancelled()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":TER?"] = "0";
        var scope = new KeysightOscilloscope(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.SingleAndWaitAsync(
                new("CHANnel1", "POSitive", 0, "NORMal"),
                new("ROLL", "NORMal"),
                TimeSpan.FromSeconds(5),
                cancellation.Token));

        Assert.Equal(("write", ":TIMebase:MODE ROLL"), transport.Commands.Last());
    }

    [Fact]
    public async Task ScreenshotRejectsNonPngWithoutReplacingTarget()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "existing");
            var transport = new ScriptedScopeTransport();
            transport.BinaryQueries[":DISPlay:DATA? PNG, COLor"] = [1, 2, 3];
            var scope = new KeysightOscilloscope(transport);

            await Assert.ThrowsAsync<WaveformIntegrityException>(
                () => scope.CaptureScreenshotAsync(path));

            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ScreenshotAcceptsPngAfterIeeeBlockPrefix()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
            var transport = new ScriptedScopeTransport();
            transport.BinaryQueries[":DISPlay:DATA? PNG, COLor"] =
                [.. "#211"u8.ToArray(), .. png];
            var scope = new KeysightOscilloscope(transport);

            await scope.CaptureScreenshotAsync(path);

            Assert.Equal(png, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ScreenshotFallsBackToAlternateKeysightCommand()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 4, 5, 6];
            var transport = new ScriptedScopeTransport();
            transport.BinaryQueries[":DISPlay:DATA? PNG, COLor"] = [1, 2, 3];
            transport.BinaryQueries[":DISPlay:DATA? PNG"] = png;
            var scope = new KeysightOscilloscope(transport);

            await scope.CaptureScreenshotAsync(path);

            Assert.Equal(png, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DrainsErrorsAndSwitchesTimebaseMode()
    {
        var transport = new ScriptedScopeTransport();
        transport.QuerySequences[":SYSTem:ERRor?"] =
            new(["-222,\"Data out of range\"", "+0,\"No error\""]);
        var scope = new KeysightOscilloscope(transport);

        IReadOnlyList<string> errors = await scope.DrainSystemErrorsAsync();
        await scope.SetTimebaseModeAsync("ROLL");

        Assert.Equal(2, errors.Count);
        Assert.Contains(("write", ":TIMebase:MODE ROLL"), transport.Commands);
    }

    [Fact]
    public async Task SavesAcquisitionChannelToSelectedReferenceSlot()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":SYSTem:ERRor?"] = "+0,\"No error\"";
        var scope = new KeysightOscilloscope(transport);

        await scope.SaveChannelToReferenceAsync("CHANnel3", 2);

        Assert.Contains(("write", ":WMEMory2:SAVE CHANnel3"), transport.Commands);
        Assert.Contains(("write", ":WMEMory2:DISPlay ON"), transport.Commands);
    }

    [Fact]
    public async Task UploadsKeysightH5AsIeeeReferenceBlock()
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".h5");
        try
        {
            await File.WriteAllBytesAsync(path, [137, 72, 68, 70, 13, 10, 26, 10, 1, 2]);
            var transport = new ScriptedScopeTransport();
            transport.Queries[":SYSTem:ERRor?"] = "+0,\"No error\"";
            var scope = new KeysightOscilloscope(transport);

            await scope.UploadReferenceWaveformAsync(path, 1);

            Assert.Contains(("write_binary", ":RECall:WMEMory1 [10 bytes]"), transport.Commands);
            Assert.Contains(("write", ":WMEMory1:DISPlay ON"), transport.Commands);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SavesReferenceH5ToDeviceCurrentStorage()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries["*OPC?"] = "1";
        transport.Queries[":SYSTem:ERRor?"] = "+0,\"No error\"";
        var scope = new KeysightOscilloscope(transport);

        await scope.SaveReferenceFileToDeviceStorageAsync("CHANnel2", "motor_start");

        Assert.Contains(("write", ":SAVE:WMEMory:SOURce CHANnel2"), transport.Commands);
        Assert.Contains(("write", ":SAVE:WMEMory \"motor_start.h5\""), transport.Commands);
        Assert.Contains(("query", "*OPC?"), transport.Commands);
    }

    [Fact]
    public async Task FetchesHardwareAndSoftwareMeasurementsWithChannelUnit()
    {
        var transport = new ScriptedScopeTransport();
        transport.Queries[":CHANnel1:UNITs?"] = "\"V\"";
        transport.Queries[":MEASure:FREQuency? CHANnel1"] = "1000";
        transport.Queries[":WAVeform:PREamble?"] = "0,0,4,1,0.001,0,0,0.1,0,0";
        transport.BinaryQueries[":WAVeform:DATA?"] = [0, 10, 0, 10];
        var scope = new KeysightOscilloscope(transport);

        IReadOnlyList<MeasurementResult> results =
            await scope.FetchMeasurementsAsync("CHANnel1", ["频率", "平均值"]);

        Assert.Equal(2, results.Count);
        Assert.Equal(1000, results[0].Value);
        Assert.All(results, result => Assert.True(result.IsValid));
    }
}
