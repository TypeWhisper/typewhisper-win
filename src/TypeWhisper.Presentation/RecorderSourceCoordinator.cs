namespace TypeWhisper.Presentation;

/// <summary>A native source may still own capture resources; its reservation must remain held until retry succeeds.</summary>
public sealed class RecorderCleanupException(string message, Exception inner) : Exception(message, inner);

/// <summary>Retains available samples and source-specific stop errors.</summary>
public sealed record RecorderSourceAudio(float[] Microphone, float[] System, IReadOnlyList<string> Warnings);

/// <summary>Starts selected sources transactionally and always attempts to stop both sources.</summary>
public sealed class RecorderSourceCoordinator(Func<Task> startMicrophone, Func<Task> startSystem,
    Func<Task<float[]>> stopMicrophone, Func<Task<float[]>> stopSystem)
{
    private bool _microphone;
    private bool _system;
    private float[] _microphoneSamples = [];
    private float[] _systemSamples = [];
    /// <summary>Starts requested sources; startup failure drains every attempted source before returning.</summary>
    public async Task StartAsync(bool microphone, bool system)
    {
        _microphoneSamples = []; _systemSamples = [];
        try
        {
            if (microphone) { _microphone = true; await startMicrophone(); }
            if (system) { _system = true; await startSystem(); }
        }
        catch { await StopAsync(); throw; }
    }
    /// <summary>Attempts both stops, retaining one source's audio even if the other fails.</summary>
    public async Task<RecorderSourceAudio> StopAsync()
    {
        var warnings = new List<string>();
        RecorderCleanupException? cleanupFailure = null;
        try
        {
            if (_microphone)
                try { _microphoneSamples = await stopMicrophone(); _microphone = false; }
                catch (RecorderCleanupException ex) { cleanupFailure = ex; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _microphone = false; warnings.Add("Microphone capture did not finish cleanly."); }
        }
        finally
        {
            if (_system)
                try { _systemSamples = await stopSystem(); _system = false; }
                catch (RecorderCleanupException ex) { cleanupFailure = ex; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _system = false; warnings.Add("System audio capture did not finish cleanly."); }
        }
        if (cleanupFailure is not null) throw cleanupFailure;
        return new(_microphoneSamples, _systemSamples, warnings);
    }
}
