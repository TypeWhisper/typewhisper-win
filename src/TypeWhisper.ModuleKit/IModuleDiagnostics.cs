namespace TypeWhisper.ModuleKit;

/// <summary>Writes events to the app's local diagnostic log, which users can export for support.</summary>
/// <remarks>
/// The log records names, never text. A stage is made of lowercase ASCII letters and digits, in parts separated by a
/// dot or a hyphen, such as <c>"sync.started"</c> or <c>"import-failed"</c>, at most 48 characters. The host records
/// it under the module's identifier, so modules cannot write events that look like the host's. A stage that breaks
/// these rules is recorded as <c>invalid-stage</c>. Of an exception, only its type, HRESULT and the method names of its
/// stack are recorded, never its message. Members can be called from any thread and never throw for an invalid stage.
/// </remarks>
public interface IModuleDiagnostics
{
    /// <summary>Records that the module reached <paramref name="stage"/>.</summary>
    /// <param name="stage">The stage name; see <see cref="IModuleDiagnostics"/>.</param>
    void Write(string stage);

    /// <summary>Records that <paramref name="stage"/> failed with <paramref name="error"/>.</summary>
    /// <param name="stage">The stage name; see <see cref="IModuleDiagnostics"/>.</param>
    /// <param name="error">The exception; its message is not recorded.</param>
    void WriteFailure(string stage, Exception error);
}
