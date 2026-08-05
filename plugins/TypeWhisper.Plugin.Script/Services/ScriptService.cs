using System.Collections.ObjectModel;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

/// <summary>Owns persisted scripts and runs the enabled chain.</summary>
public sealed class ScriptService : IDisposable
{
    private readonly IPluginHostServices _host;
    private readonly IScriptConfigurationStore _store;
    private readonly IScriptProcessRunner _runner;

    /// <summary>Initializes the service from the plugin data directory.</summary>
    public ScriptService(IPluginHostServices host)
        : this(
            host,
            new ScriptConfigurationStore(host.PluginDataDirectory),
            new ScriptProcessRunner())
    {
    }

    internal ScriptService(
        IPluginHostServices host,
        IScriptConfigurationStore store,
        IScriptProcessRunner runner)
    {
        _host = host;
        _store = store;
        _runner = runner;
        var loaded = store.Load();
        LoadError = loaded.Error;
        foreach (var script in loaded.Scripts)
            Scripts.Add(script);

        if (LoadError is not null)
            _host.Log(PluginLogLevel.Warning, $"Failed to load script configuration: {LoadError}");
    }

    /// <summary>Gets the ordered script list.</summary>
    public ObservableCollection<ScriptEntry> Scripts { get; } = [];

    /// <summary>Gets the non-destructive configuration load error, if any.</summary>
    public string? LoadError { get; }

    /// <summary>Gets whether mutations are blocked to protect an unreadable configuration.</summary>
    public bool IsReadOnly => LoadError is not null;

    internal IPluginLocalization? Localization => _host.Localization;

    internal IScriptProcessRunner Runner => _runner;

    /// <summary>Adds and persists one script.</summary>
    public void AddScript(ScriptEntry script)
    {
        EnsureWritable();
        Scripts.Add(script);
        SaveOrRollback(() => Scripts.Remove(script));
    }

    /// <summary>Removes and persists one script.</summary>
    public void RemoveScript(Guid id)
    {
        EnsureWritable();
        var index = IndexOf(id);
        if (index < 0)
            return;

        var removed = Scripts[index];
        Scripts.RemoveAt(index);
        SaveOrRollback(() => Scripts.Insert(index, removed));
    }

    /// <summary>Updates and persists one script.</summary>
    public void UpdateScript(ScriptEntry updated)
    {
        EnsureWritable();
        var index = IndexOf(updated.Id);
        if (index < 0)
            return;

        var previous = Scripts[index];
        Scripts[index] = updated;
        SaveOrRollback(() => Scripts[index] = previous);
    }

    /// <summary>Moves a script up and persists the order.</summary>
    public void MoveUp(Guid id)
    {
        EnsureWritable();
        var index = IndexOf(id);
        if (index <= 0)
            return;

        Scripts.Move(index, index - 1);
        SaveOrRollback(() => Scripts.Move(index - 1, index));
    }

    /// <summary>Moves a script down and persists the order.</summary>
    public void MoveDown(Guid id)
    {
        EnsureWritable();
        var index = IndexOf(id);
        if (index < 0 || index >= Scripts.Count - 1)
            return;

        Scripts.Move(index, index + 1);
        SaveOrRollback(() => Scripts.Move(index + 1, index));
    }

    internal void MoveTo(Guid id, int targetIndex)
    {
        EnsureWritable();
        var index = IndexOf(id);
        if (index < 0 || Scripts.Count == 0)
            return;

        targetIndex = Math.Clamp(targetIndex, 0, Scripts.Count - 1);
        if (targetIndex == index)
            return;

        Scripts.Move(index, targetIndex);
        SaveOrRollback(() => Scripts.Move(targetIndex, index));
    }

    /// <summary>Persists the current list.</summary>
    public void Save()
    {
        EnsureWritable();
        _store.Save(Scripts.ToList());
    }

    /// <summary>Runs enabled scripts sequentially with fail-open behavior.</summary>
    public async Task<string> RunScriptsAsync(
        string text,
        PostProcessingContext context,
        CancellationToken cancellationToken)
    {
        var current = text;
        foreach (var script in Scripts.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!script.IsEnabled)
                continue;

            ScriptExecutionResult result;
            try
            {
                result = await _runner.RunAsync(script, current, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _host.Log(PluginLogLevel.Warning, $"Script '{script.Name}' failed: {LimitLog(ex.Message)}");
                continue;
            }
            if (result.IsSuccess)
            {
                current = result.Output;
                if (!string.IsNullOrEmpty(result.Error))
                    _host.Log(PluginLogLevel.Info, $"Script '{script.Name}' stderr: {LimitLog(result.Error)}");
                continue;
            }

            _host.Log(
                PluginLogLevel.Warning,
                $"Script '{script.Name}' failed ({result.Status}, exit {result.ExitCode?.ToString() ?? "n/a"}): " +
                LimitLog(result.Error));
        }

        return current;
    }

    /// <summary>Releases service resources.</summary>
    public void Dispose()
    {
        if (_runner is IDisposable disposable)
            disposable.Dispose();
    }

    private static string LimitLog(string value) => value.Length <= 2048 ? value : value[..2048] + "...";

    private int IndexOf(Guid id)
    {
        for (var index = 0; index < Scripts.Count; index++)
        {
            if (Scripts[index].Id == id)
                return index;
        }

        return -1;
    }

    private void SaveOrRollback(Action rollback)
    {
        try
        {
            _store.Save(Scripts.ToList());
        }
        catch
        {
            rollback();
            throw;
        }
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
            throw new InvalidOperationException("Script configuration is read-only because it could not be loaded.");
    }
}
