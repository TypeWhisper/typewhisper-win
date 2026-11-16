namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

/// <summary>
/// A test that needs the pinned model files, which are not part of the repository. Set
/// <c>TYPEWHISPER_NEMOTRON_MODEL_DIR</c> to a directory with <c>model.onnx</c> and <c>model.onnx_data</c> of the
/// pinned revision to run it; it is skipped otherwise.
/// </summary>
public sealed class NemotronModelFactAttribute : FactAttribute
{
    internal static string? ModelDirectory =>
        Environment.GetEnvironmentVariable("TYPEWHISPER_NEMOTRON_MODEL_DIR") is { Length: > 0 } directory
        && File.Exists(Path.Combine(directory, "model.onnx")) && File.Exists(Path.Combine(directory, "model.onnx_data"))
            ? directory : null;

    public NemotronModelFactAttribute()
    {
        if (ModelDirectory is null) Skip = "Set TYPEWHISPER_NEMOTRON_MODEL_DIR to the pinned model files to run this test.";
    }
}
