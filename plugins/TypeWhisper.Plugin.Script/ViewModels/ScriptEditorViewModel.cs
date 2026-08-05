using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

internal sealed record ScriptExample(
    string DisplayName,
    string ScriptName,
    string Shell,
    string Command);

internal sealed class ScriptEditorViewModel : ObservableObject, IDisposable
{
    private readonly ScriptService _service;
    private readonly IScriptConfirmationService _confirmations;
    private readonly ScriptEntry? _original;
    private readonly string _baselineName;
    private readonly string _baselineCommand;
    private readonly string _baselineShell;
    private readonly string _baselineTimeout;
    private string _name;
    private string _command;
    private string _shell;
    private string _timeout;
    private string _validationMessage = "";
    private string _testInput = "Test transcription";
    private string _testStatus = "";
    private string _testOutput = "";
    private string _testError = "";
    private string _testExitCode = "";
    private string _testElapsed = "";
    private bool _isTestRunning;
    private CancellationTokenSource? _testCancellation;
    private int _testRunId;

    internal ScriptEditorViewModel(
        ScriptService service,
        ScriptEntry? original,
        IScriptConfirmationService confirmations)
    {
        _service = service;
        _original = original;
        _baselineName = original?.Name ?? "";
        _baselineCommand = original?.Command ?? "";
        _baselineShell = original?.Shell ?? ScriptShells.CommandPrompt;
        _baselineTimeout = (original?.TimeoutSeconds ?? ScriptDefaults.TimeoutSeconds)
            .ToString(CultureInfo.InvariantCulture);
        _name = _baselineName;
        _command = _baselineCommand;
        _shell = _baselineShell;
        _timeout = _baselineTimeout;
        _confirmations = confirmations;

        foreach (var shell in ScriptShells.Supported)
            ShellOptions.Add(shell);
        EnsureShellOption(_shell);
        Examples.Add(new ScriptExample(
            L("Settings.Example.Uppercase"),
            L("Settings.Example.Uppercase"),
            ScriptShells.WindowsPowerShell,
            "$text = [Console]::In.ReadToEnd(); [Console]::Out.Write($text.ToUpperInvariant())"));
        Examples.Add(new ScriptExample(
            L("Settings.Example.Lowercase"),
            L("Settings.Example.Lowercase"),
            ScriptShells.WindowsPowerShell,
            "$text = [Console]::In.ReadToEnd(); [Console]::Out.Write($text.ToLowerInvariant())"));
        Examples.Add(new ScriptExample(
            L("Settings.Example.Trim"),
            L("Settings.Example.Trim"),
            ScriptShells.WindowsPowerShell,
            "$text = [Console]::In.ReadToEnd(); [Console]::Out.Write($text.Trim())"));

        SaveCommand = new RelayCommand(SaveAndRequestClose, () => IsDirty && !_service.IsReadOnly);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ApplyExampleCommand = new RelayCommand<ScriptExample>(ApplyExample);
        RunTestCommand = new RelayCommand(() => _ = RunTestAsync());
        CancelTestCommand = new RelayCommand(CancelTest, () => IsTestRunning);

        if (!ScriptShells.IsSupported(_shell))
            ValidationMessage = L("Settings.UnsupportedShell", _shell);
    }

    internal event EventHandler? CloseRequested;
    internal bool IsNew => _original is null;
    internal ScriptEntry? SavedScript { get; private set; }
    public ObservableCollection<string> ShellOptions { get; } = [];
    public ObservableCollection<ScriptExample> Examples { get; } = [];
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasTestOutput => !string.IsNullOrEmpty(TestOutput);
    public bool HasTestError => !string.IsNullOrEmpty(TestError);

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) DraftChanged(); }
    }

    public string Command
    {
        get => _command;
        set { if (SetProperty(ref _command, value)) DraftChanged(); }
    }

    public string Shell
    {
        get => _shell;
        set { if (SetProperty(ref _shell, value ?? "")) DraftChanged(); }
    }

    public string Timeout
    {
        get => _timeout;
        set { if (SetProperty(ref _timeout, value)) DraftChanged(); }
    }

    public bool IsDirty => Name != _baselineName
        || Command != _baselineCommand
        || Shell != _baselineShell
        || Timeout != _baselineTimeout;

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
                OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public string TestInput { get => _testInput; set => SetProperty(ref _testInput, value); }
    public string TestStatus { get => _testStatus; private set => SetProperty(ref _testStatus, value); }

    public string TestOutput
    {
        get => _testOutput;
        private set
        {
            if (SetProperty(ref _testOutput, value))
                OnPropertyChanged(nameof(HasTestOutput));
        }
    }

    public string TestError
    {
        get => _testError;
        private set
        {
            if (SetProperty(ref _testError, value))
                OnPropertyChanged(nameof(HasTestError));
        }
    }

    public string TestExitCode { get => _testExitCode; private set => SetProperty(ref _testExitCode, value); }
    public string TestElapsed { get => _testElapsed; private set => SetProperty(ref _testElapsed, value); }

    public bool IsTestRunning
    {
        get => _isTestRunning;
        private set
        {
            if (SetProperty(ref _isTestRunning, value)
                && CancelTestCommand is RelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplyExampleCommand { get; }
    public ICommand RunTestCommand { get; }
    public ICommand CancelTestCommand { get; }

    internal string L(string key, params object[] args)
    {
        var localization = _service.Localization;
        return localization is null ? key : localization.GetString(key, args);
    }

    internal bool Save()
    {
        if (!TryCreateScript(out var script))
            return false;

        try
        {
            if (_original is null)
                _service.AddScript(script);
            else
                _service.UpdateScript(script);
            SavedScript = script;
            ValidationMessage = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ValidationMessage = L("Settings.SaveFailed", ex.Message);
            return false;
        }
    }

    internal bool CanClose()
    {
        if (!IsDirty || SavedScript is not null)
        {
            CancelTest();
            return true;
        }

        var displayName = string.IsNullOrWhiteSpace(Name) ? L("Settings.NewScript") : Name;
        switch (_confirmations.ConfirmUnsavedChanges(displayName))
        {
            case ConfirmationChoice.Primary:
                if (!Save())
                    return false;
                break;
            case ConfirmationChoice.Secondary:
                break;
            default:
                return false;
        }

        CancelTest();
        return true;
    }

    internal async Task RunTestAsync()
    {
        if (!TryCreateScript(out var script))
            return;

        CancelTest();
        var runId = ++_testRunId;
        var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        IsTestRunning = true;
        TestStatus = L("Settings.TestRunning");
        TestOutput = "";
        TestError = "";
        TestExitCode = "";
        TestElapsed = "";

        try
        {
            var result = await _service.Runner.RunAsync(
                script,
                TestInput,
                new PostProcessingContext(),
                cancellation.Token);
            if (runId != _testRunId)
                return;
            TestStatus = L($"Settings.TestStatus.{result.Status}");
            TestOutput = result.Output;
            TestError = result.Error;
            TestExitCode = result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? L("Settings.NotAvailable");
            TestElapsed = L("Settings.TestElapsedValue", result.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            if (runId == _testRunId)
            {
                TestStatus = L("Settings.TestCancelled");
                TestError = "";
                TestExitCode = L("Settings.NotAvailable");
            }
        }
        catch (Exception ex)
        {
            if (runId == _testRunId)
            {
                TestStatus = L("Settings.TestStatus.Failed");
                TestError = ex.Message;
                TestExitCode = L("Settings.NotAvailable");
            }
        }
        finally
        {
            cancellation.Dispose();
            if (runId == _testRunId)
            {
                _testCancellation = null;
                IsTestRunning = false;
            }
        }
    }

    internal void CancelTest() => _testCancellation?.Cancel();

    public void Dispose()
    {
        CancelTest();
        _testCancellation = null;
    }

    private void SaveAndRequestClose()
    {
        if (Save())
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyExample(ScriptExample example)
    {
        CancelTest();
        Name = example.ScriptName;
        Shell = example.Shell;
        Command = example.Command;
    }

    private bool TryCreateScript(out ScriptEntry script)
    {
        script = default!;
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationMessage = L("Settings.NameRequired");
            return false;
        }
        if (string.IsNullOrWhiteSpace(Command))
        {
            ValidationMessage = L("Settings.CommandRequired");
            return false;
        }
        if (!ScriptShells.IsSupported(Shell))
        {
            ValidationMessage = L("Settings.UnsupportedShell", Shell);
            return false;
        }
        if (!int.TryParse(Timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
            || timeout is < ScriptDefaults.MinimumTimeoutSeconds or > ScriptDefaults.MaximumTimeoutSeconds)
        {
            ValidationMessage = L(
                "Settings.TimeoutRange",
                ScriptDefaults.MinimumTimeoutSeconds,
                ScriptDefaults.MaximumTimeoutSeconds);
            return false;
        }

        script = new ScriptEntry
        {
            Id = _original?.Id ?? Guid.NewGuid(),
            Name = Name,
            Command = Command,
            Shell = ScriptShells.Normalize(Shell),
            IsEnabled = _original?.IsEnabled ?? true,
            TimeoutSeconds = timeout
        };
        return true;
    }

    private void DraftChanged()
    {
        ValidationMessage = ScriptShells.IsSupported(Shell)
            ? ""
            : L("Settings.UnsupportedShell", Shell);
        OnPropertyChanged(nameof(IsDirty));
        if (SaveCommand is RelayCommand save)
            save.RaiseCanExecuteChanged();
    }

    private void EnsureShellOption(string shell)
    {
        if (!string.IsNullOrWhiteSpace(shell)
            && !ShellOptions.Any(option => option.Equals(shell, StringComparison.OrdinalIgnoreCase)))
        {
            ShellOptions.Add(shell);
        }
    }
}
