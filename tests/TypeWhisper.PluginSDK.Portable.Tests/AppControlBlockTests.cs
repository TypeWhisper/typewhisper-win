using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.PluginHost;

// Smart App Control refuses unsigned native plugin files with these codes (issue #823).
public sealed class AppControlBlockTests
{
    private const string SherpaLoaderMessage =
        "Unable to load the sherpa-onnx native runtime. Expected sherpa-onnx-c-api.dll and sherpaort.dll under 'C:\\p\\runtimes\\win-x64\\native'. "
        + "Reinstall the sherpa-onnx plugin or update it from the plugin marketplace. Loader error: Unable to load DLL "
        + "'C:\\p\\runtimes\\win-x64\\native\\sherpa-onnx-c-api.dll' or one of its dependencies: An Application Control policy has blocked this file. (0x800711C7)";

    [Theory]
    [InlineData(4551)]
    [InlineData(4556)]
    [InlineData(4559)]
    public void RecognizesAProcessThatWindowsRefusedToStart(int code) =>
        Assert.True(AppControlBlock.IsBlock(new InvalidOperationException("The engine could not start.", new Win32Exception(code))));

    [Fact]
    public void RecognizesTheLoaderCodeInAWrappedMessageAndKeepsTheMessageThatNamesTheFile()
    {
        var loader = new DllNotFoundException("Unable to load DLL 'transcribe.dll' or one of its dependencies: Eine Anwendungssteuerungsrichtlinie hat diese Datei blockiert. (0x800711c7)");
        var wrapped = new InvalidOperationException("The model could not be loaded.", loader);

        Assert.Same(loader, AppControlBlock.Find(wrapped));
        Assert.Same(loader, AppControlBlock.Find(new AggregateException(new TimeoutException(), wrapped)));
    }

    [Fact]
    public void RecognizesTheResultCodeOfAComException() =>
        Assert.True(AppControlBlock.IsBlock(new COMException("Blocked", unchecked((int)0x800711C7))));

    [Fact]
    public void LeavesOtherLoadFailuresAlone()
    {
        Assert.False(AppControlBlock.IsBlock(new DllNotFoundException("Unable to load DLL 'whisper.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)")));
        Assert.False(AppControlBlock.IsBlock(new Win32Exception(2)));
        Assert.False(AppControlBlock.IsBlock(null));
    }

    [Fact]
    public void TheWorkerReportsABlockWithItsOwnKindAndTheHostExplainsIt()
    {
        var error = TranscriptionWorkerProtocol.ToError(new DllNotFoundException(SherpaLoaderMessage));
        Assert.Equal("app-control", error.Kind);
        Assert.Equal(SherpaLoaderMessage, error.Message);

        var shown = Assert.IsType<AppControlBlockedException>(TranscriptionWorkerProtocol.ToException(error));
        Assert.IsAssignableFrom<InvalidOperationException>(shown);
        Assert.Equal(AppControlBlock.Message, shown.Message);
        Assert.Equal(SherpaLoaderMessage, shown.InnerException?.Message);
    }

    [Fact]
    public void ACanceledRequestStaysCanceled() =>
        Assert.Equal("canceled", TranscriptionWorkerProtocol.ToError(new OperationCanceledException(SherpaLoaderMessage)).Kind);
}
