using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.PluginHost;

// Smart App Control refuses unsigned native plugin files with these codes (issue #823).
public sealed class AppControlBlockTests
{
    // What the sherpa-onnx plugin reports: the loader names the file and ends with Windows' text for the error, in the
    // user's language and without its code.
    private static string SherpaLoaderMessage =>
        "Unable to load the sherpa-onnx native runtime. Expected sherpa-onnx-c-api.dll and sherpaort.dll under 'C:\\p\\runtimes\\win-x64\\native'. "
        + "Reinstall the sherpa-onnx plugin or update it from the plugin marketplace. Loader error: Unable to load DLL "
        + "'C:\\p\\runtimes\\win-x64\\native\\sherpa-onnx-c-api.dll' or one of its dependencies: " + new Win32Exception(4551).Message;

    [Theory]
    [InlineData(4551)]
    [InlineData(4556)]
    [InlineData(4559)]
    public void RecognizesAProcessThatWindowsRefusedToStart(int code) =>
        Assert.True(AppControlBlock.IsBlock(new InvalidOperationException("The engine could not start.", new Win32Exception(code))));

    [WindowsFact]
    public void RecognizesWindowsTextForTheErrorAndKeepsTheMessageThatNamesTheFile()
    {
        var loader = new DllNotFoundException("Unable to load DLL 'C:\\p\\transcribe.dll' or one of its dependencies: " + new Win32Exception(4551).Message);
        var wrapped = new InvalidOperationException("The model could not be loaded.", loader);

        Assert.Same(loader, AppControlBlock.Find(wrapped));
        Assert.Same(loader, AppControlBlock.Find(new AggregateException(new TimeoutException(), wrapped)));
    }

    [Fact]
    public void AnAggregatesChildrenShareTheDepthLimit()
    {
        Exception deep = new Win32Exception(4551);
        for (var level = 0; level < 40; level++) deep = new AggregateException(new InvalidOperationException("Wrapper", deep));

        Assert.Null(AppControlBlock.Find(deep));
        Assert.True(AppControlBlock.IsBlock(new AggregateException(new InvalidOperationException("Wrapper", new Win32Exception(4551)))));
    }

    [Fact]
    public void TheWorkerKeepsThePluginsMessageWhenTheBlockIsFoundDeeper()
    {
        var error = TranscriptionWorkerProtocol.ToError(new DllNotFoundException("Unable to load 'C:\\p\\crispasr.exe'.", new Win32Exception(4551)));

        Assert.Equal("app-control", error.Kind);
        Assert.StartsWith("Unable to load 'C:\\p\\crispasr.exe'. ", error.Message);
        Assert.EndsWith(new Win32Exception(4551).Message, error.Message);
    }

    [Fact]
    public void RecognizesTheResultCodeOfAComException() =>
        Assert.True(AppControlBlock.IsBlock(new COMException("Blocked", unchecked((int)0x800711C7))));

    [Fact]
    public void LeavesOtherLoadFailuresAlone()
    {
        Assert.False(AppControlBlock.IsBlock(new DllNotFoundException("Unable to load DLL 'whisper.dll' or one of its dependencies: " + new Win32Exception(126).Message)));
        Assert.False(AppControlBlock.IsBlock(new Win32Exception(2)));
        Assert.False(AppControlBlock.IsBlock(null));
    }

    [WindowsFact]
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
        Assert.Equal("canceled", TranscriptionWorkerProtocol.ToError(new OperationCanceledException("Stopped.", new Win32Exception(4551))).Kind);
}
