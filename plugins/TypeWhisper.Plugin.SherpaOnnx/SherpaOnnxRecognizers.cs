using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal static class SherpaOnnxRecognizers
{
    /// <summary>
    /// Creates a recognizer and fails right away when sherpa-onnx could not load the model.
    /// </summary>
    /// <remarks>
    /// Since sherpa-onnx 2.0 a model that cannot be loaded (for example a damaged file) no longer throws. The
    /// recognizer keeps a null native handle, and the failure would only show up as a NullReferenceException on
    /// the first dictation. The wrapper does not expose that handle, but a stream created from a null recognizer
    /// has a null handle too.
    /// </remarks>
    public static OfflineRecognizer Create(OfflineRecognizerConfig config, string modelName)
    {
        var recognizer = new OfflineRecognizer(config);
        bool loaded;
        using (var probe = recognizer.CreateStream())
            loaded = probe.Handle != IntPtr.Zero;
        if (loaded)
            return recognizer;

        recognizer.Dispose();
        throw new InvalidOperationException(
            $"{modelName} could not be loaded. The model files may be damaged; remove the model and download it again.");
    }
}
