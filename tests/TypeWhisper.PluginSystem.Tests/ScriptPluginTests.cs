namespace TypeWhisper.PluginSystem.Tests;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Moq;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class ScriptPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifest()
    {
        var manifestPath = FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        Assert.Equal(manifest.RootElement.GetProperty("version").GetString(), new ScriptPlugin().PluginVersion);
        Assert.Equal("1.1.0", new ScriptPlugin().PluginVersion);
        Assert.True(manifest.RootElement.GetProperty("isLocal").GetBoolean());
    }

    [Fact]
    public void Editor_AllowsMultipleEditsWithoutChangingPersistedScript()
    {
        RunInSta(() =>
        {
            using var fixture = new ScriptFixture(
                new ScriptEntry { Name = "Original", Command = "echo original" });
            var original = Assert.Single(fixture.Service.Scripts);
            var window = new ScriptEditorWindow(fixture.Service, original);
            var name = Assert.IsType<TextBox>(window.FindName("NameBox"));
            var command = Assert.IsType<TextBox>(window.FindName("CommandBox"));
            MeasureWindow(window, 660, 640);

            name.Text = "E";
            name.Text = "Ed";
            name.Text = "Edi";
            name.Text = "Ed";
            command.Text = "echo changed";

            Assert.Equal("Ed", window.ViewModel.Name);
            Assert.Equal("echo changed", window.ViewModel.Command);
            Assert.Equal("Original", fixture.Service.Scripts[0].Name);
            Assert.True(window.ViewModel.IsDirty);
            window.ViewModel.Dispose();
        });
    }

    [Fact]
    public void ViewModel_StagesSaveAndCancelWithoutGhostEntries()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Original", Command = "echo original" });
        var original = Assert.Single(fixture.Service.Scripts);
        using var editor = fixture.CreateEditor(original);
        editor.Name = "Edited";

        Assert.Equal("Original", fixture.Service.Scripts[0].Name);
        Assert.True(editor.Save());
        Assert.Equal("Edited", fixture.Service.Scripts[0].Name);
        Assert.Equal(1, fixture.Store.SaveCount);

        using var discarded = fixture.CreateEditor(fixture.Service.Scripts[0]);
        discarded.Name = "Discarded";
        fixture.Dialogs.UnsavedChoice = ConfirmationChoice.Secondary;
        Assert.True(discarded.CanClose());
        Assert.Equal("Edited", fixture.Service.Scripts[0].Name);

        using var newDraft = fixture.CreateEditor(null);
        newDraft.Name = "Never saved";
        newDraft.Command = "echo never";
        Assert.True(newDraft.CanClose());
        Assert.Single(fixture.Service.Scripts);
    }

    [Theory]
    [InlineData(0, "Changed", true)]
    [InlineData(1, "Original", true)]
    [InlineData(2, "Original", false)]
    public void ViewModel_DirtyCloseHonorsChoice(
        int choiceValue,
        string persistedName,
        bool canClose)
    {
        var choice = (ConfirmationChoice)choiceValue;
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Original", Command = "echo original" });
        fixture.Dialogs.UnsavedChoice = choice;
        using var viewModel = fixture.CreateEditor(Assert.Single(fixture.Service.Scripts));
        viewModel.Name = "Changed";

        Assert.Equal(canClose, viewModel.CanClose());
        Assert.Equal(persistedName, fixture.Service.Scripts[0].Name);
    }

    [Fact]
    public void ViewModel_UnknownShellRemainsVisibleAndCannotBeSaved()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Legacy", Command = "echo ok", Shell = "custom-shell" });
        using var viewModel = fixture.CreateEditor(Assert.Single(fixture.Service.Scripts));
        viewModel.Name = "Changed";

        Assert.Equal("custom-shell", viewModel.Shell);
        Assert.False(viewModel.Save());
        Assert.Contains("custom-shell", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal("custom-shell", fixture.Service.Scripts[0].Shell);
    }

    [Fact]
    public void SettingsViewModel_AddAndEditOnlyReloadAfterEditorSave()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "First", Command = "one" });
        var viewModel = fixture.CreateViewModel();
        var selected = Assert.Single(viewModel.Items);
        viewModel.SelectedItem = selected;

        viewModel.AddCommand.Execute(null);
        Assert.Null(fixture.EditorHost.LastScript);
        Assert.Single(viewModel.Items);

        fixture.EditorHost.Handler = script =>
        {
            var updated = script! with { Name = "Edited" };
            fixture.Service.UpdateScript(updated);
            return updated.Id;
        };
        viewModel.EditCommand.Execute(null);

        Assert.Same(selected, viewModel.SelectedItem);
        Assert.Equal("Edited", selected.Name);
        Assert.Equal("Edited", fixture.Service.Scripts[0].Name);
    }

    [Fact]
    public void ViewModel_MoveToggleAndRemoveKeepStableSelection()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "First", Command = "one" },
            new ScriptEntry { Name = "Second", Command = "two" });
        var viewModel = fixture.CreateViewModel();
        var selected = viewModel.Items[1];
        viewModel.SelectedItem = selected;

        viewModel.MoveUpCommand.Execute(null);
        selected.IsEnabled = false;

        Assert.Same(selected, viewModel.SelectedItem);
        Assert.Same(selected, viewModel.Items[0]);
        Assert.False(fixture.Service.Scripts[0].IsEnabled);

        fixture.Dialogs.RemoveConfirmed = false;
        viewModel.RemoveCommand.Execute(null);
        Assert.Equal(2, viewModel.Items.Count);

        fixture.Dialogs.RemoveConfirmed = true;
        viewModel.RemoveCommand.Execute(null);
        Assert.Single(viewModel.Items);
        Assert.Single(fixture.Service.Scripts);
    }

    [Fact]
    public void ViewModel_DragMovePersistsOnceAndKeepsStableSelection()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "First", Command = "one" },
            new ScriptEntry { Name = "Second", Command = "two" },
            new ScriptEntry { Name = "Third", Command = "three" });
        var viewModel = fixture.CreateViewModel();
        var moved = viewModel.Items[0];
        viewModel.SelectedItem = moved;

        viewModel.MoveItem(moved, 2);

        Assert.Equal(["Second", "Third", "First"], fixture.Service.Scripts.Select(item => item.Name));
        Assert.Same(moved, viewModel.SelectedItem);
        Assert.Same(moved, viewModel.Items[2]);
        Assert.Equal(1, fixture.Store.SaveCount);

        viewModel.MoveItem(moved, 2);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    [Fact]
    public void ViewModel_ReloadToleratesDuplicateLegacyIds()
    {
        var duplicateId = Guid.NewGuid();
        using var fixture = new ScriptFixture(
            new ScriptEntry { Id = duplicateId, Name = "First", Command = "one" },
            new ScriptEntry { Id = duplicateId, Name = "Second", Command = "two" });
        var viewModel = fixture.CreateViewModel();

        viewModel.MoveItem(viewModel.Items[0], 1);

        Assert.Equal(2, viewModel.Items.Count);
        Assert.Equal(["Second", "First"], viewModel.Items.Select(item => item.Name));
    }

    [Fact]
    public void ViewModel_DropIndicatorDistinguishesBeforeAfterAndClears()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Target", Command = "one" });
        var item = Assert.Single(fixture.CreateViewModel().Items);

        item.SetDropIndicator(true, false);
        Assert.True(item.IsDropTarget);
        Assert.True(item.ShowDropBefore);
        Assert.False(item.ShowDropAfter);

        item.SetDropIndicator(true, true);
        Assert.False(item.ShowDropBefore);
        Assert.True(item.ShowDropAfter);

        item.SetDropIndicator(false, false);
        Assert.False(item.IsDropTarget);
        Assert.False(item.ShowDropBefore);
        Assert.False(item.ShowDropAfter);
    }

    [Fact]
    public void ViewModel_LoadFailureIsVisibleAndDisablesMutations()
    {
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(item => item.Localization).Returns(new TestLocalization());
        using var service = new ScriptService(host.Object, new ErrorStore("broken json"), new FakeRunner());
        var viewModel = new ScriptSettingsViewModel(service, new FakeEditorHost(), new FakeDialogs());

        Assert.True(viewModel.IsReadOnly);
        Assert.True(viewModel.HasLoadError);
        Assert.Contains("broken json", viewModel.LoadErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.AddCommand.CanExecute(null));
    }

    [Fact]
    public void ViewModel_ReadOnlyToggleRestoresPersistedValue()
    {
        var script = new ScriptEntry { Name = "Protected", Command = "more", IsEnabled = true };
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(item => item.Localization).Returns(new TestLocalization());
        using var service = new ScriptService(
            host.Object,
            new ErrorStore("broken json", [script]),
            new FakeRunner());
        var viewModel = new ScriptSettingsViewModel(service, new FakeEditorHost(), new FakeDialogs());
        var item = Assert.Single(viewModel.Items);

        item.IsEnabled = false;

        Assert.True(item.IsEnabled);
        Assert.True(Assert.Single(service.Scripts).IsEnabled);
    }

    [Fact]
    public async Task ViewModel_TestRunnerUsesDraftWithoutPersistingIt()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Original", Command = "old" });
        fixture.Runner.NextResult = new ScriptExecutionResult(
            ScriptExecutionStatus.Success, "TESTED", "", 0, TimeSpan.FromMilliseconds(12));
        using var viewModel = fixture.CreateEditor(Assert.Single(fixture.Service.Scripts));
        viewModel.Command = "unsaved";
        viewModel.TestInput = "sample";

        await viewModel.RunTestAsync();

        Assert.Equal("unsaved", fixture.Runner.LastScript!.Command);
        Assert.Equal("sample", fixture.Runner.LastInput);
        Assert.Equal("TESTED", viewModel.TestOutput);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal("old", fixture.Service.Scripts[0].Command);
    }

    [Fact]
    public async Task ViewModel_StartingNewTestCancelsPreviousRun()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "Test", Command = "command" });
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Runner.Handler = async (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ScriptExecutionResult(
                ScriptExecutionStatus.Success, "second", "", 0, TimeSpan.Zero);
        };
        using var viewModel = fixture.CreateEditor(Assert.Single(fixture.Service.Scripts));

        var first = viewModel.RunTestAsync();
        await firstStarted.Task;
        var second = viewModel.RunTestAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(2, calls);
        Assert.Equal("second", viewModel.TestOutput);
        Assert.False(viewModel.IsTestRunning);
    }

    [Fact]
    public void Editor_ExampleFillsDraftWithoutPersisting()
    {
        using var fixture = new ScriptFixture();
        using var viewModel = fixture.CreateEditor(null);
        var example = Assert.Single(viewModel.Examples, item => item.DisplayName.Contains("Uppercase", StringComparison.Ordinal));

        viewModel.ApplyExampleCommand.Execute(example);

        Assert.Equal(example.ScriptName, viewModel.Name);
        Assert.Equal("powershell", viewModel.Shell);
        Assert.Contains("ToUpperInvariant", viewModel.Command, StringComparison.Ordinal);
        Assert.True(viewModel.IsDirty);
        Assert.Empty(fixture.Service.Scripts);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public void ConfigurationStore_LoadsLegacyJsonWithDefaultTimeoutAndSavesAtomically()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "scripts.json");
            File.WriteAllText(path, """
                [{"id":"3d648840-2b7f-4ee7-9146-3ebeb487009c","name":"Legacy","command":"more","shell":"cmd","isEnabled":true}]
                """);
            var store = new ScriptConfigurationStore(directory);

            var loaded = store.Load();
            Assert.Null(loaded.Error);
            Assert.Equal(5, Assert.Single(loaded.Scripts).TimeoutSeconds);

            store.Save(loaded.Scripts.ToList());
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(5, json.RootElement[0].GetProperty("timeoutSeconds").GetInt32());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationStore_CorruptJsonIsNotOverwritten()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "scripts.json");
            const string corrupt = "{ definitely not json";
            File.WriteAllText(path, corrupt);
            var store = new ScriptConfigurationStore(directory);

            var loaded = store.Load();

            Assert.NotNull(loaded.Error);
            Assert.Empty(loaded.Scripts);
            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationStore_IgnoresNullEntriesWithoutDiscardingValidScripts()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "scripts.json"),
                """
                [null,{"name":"Valid","command":"more","shell":"cmd","isEnabled":true}]
                """);
            var store = new ScriptConfigurationStore(directory);

            var loaded = store.Load();

            Assert.Null(loaded.Error);
            Assert.Equal("Valid", Assert.Single(loaded.Scripts).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptService_FailedScriptKeepsTextAndContinues()
    {
        using var fixture = new ScriptFixture(
            new ScriptEntry { Name = "First", Command = "one" },
            new ScriptEntry { Name = "Second", Command = "two" },
            new ScriptEntry { Name = "Third", Command = "three" });
        fixture.Runner.Results.Enqueue(new ScriptExecutionResult(
            ScriptExecutionStatus.Success, "after-first", "", 0, TimeSpan.Zero));
        fixture.Runner.Results.Enqueue(new ScriptExecutionResult(
            ScriptExecutionStatus.Failed, "ignored", "bad", 7, TimeSpan.Zero));
        fixture.Runner.Results.Enqueue(new ScriptExecutionResult(
            ScriptExecutionStatus.Success, "after-third", "", 0, TimeSpan.Zero));

        var result = await fixture.Service.RunScriptsAsync("initial", new PostProcessingContext(), CancellationToken.None);

        Assert.Equal("after-third", result);
        Assert.Equal(["initial", "after-first", "after-first"], fixture.Runner.Inputs);
    }

    [Fact]
    public async Task ProcessRunner_CommandPromptPreservesUtf8AndEnvironment()
    {
        var runner = new ScriptProcessRunner();
        var script = new ScriptEntry
        {
            Name = "cmd",
            Shell = "cmd",
            Command = "echo %TYPEWHISPER_APP_NAME% & more",
            TimeoutSeconds = 5
        };

        var result = await runner.RunAsync(
            script,
            "Grüße 世界",
            new PostProcessingContext { ActiveAppName = "Notepad" },
            CancellationToken.None);

        Assert.Equal(ScriptExecutionStatus.Success, result.Status);
        Assert.Contains("Notepad", result.Output, StringComparison.Ordinal);
        Assert.Contains("Grüße 世界", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunner_SuccessDoesNotRequireCommandToConsumeStandardInput()
    {
        var runner = new ScriptProcessRunner();
        var result = await runner.RunAsync(
            new ScriptEntry { Name = "early exit", Command = "echo done", TimeoutSeconds = 5 },
            new string('x', 2 * 1024 * 1024),
            new PostProcessingContext(),
            CancellationToken.None);

        Assert.Equal(ScriptExecutionStatus.Success, result.Status);
        Assert.Contains("done", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunner_NonZeroExitAndTimeoutReturnFailures()
    {
        var runner = new ScriptProcessRunner();

        var failed = await runner.RunAsync(
            new ScriptEntry { Name = "fail", Command = "echo error 1>&2 & exit /b 7" },
            "input",
            new PostProcessingContext(),
            CancellationToken.None);
        var timedOut = await runner.RunAsync(
            new ScriptEntry { Name = "timeout", Command = "ping 127.0.0.1 -n 6 >nul", TimeoutSeconds = 1 },
            "input",
            new PostProcessingContext(),
            CancellationToken.None);

        Assert.Equal(ScriptExecutionStatus.Failed, failed.Status);
        Assert.Equal(7, failed.ExitCode);
        Assert.Contains("error", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScriptExecutionStatus.TimedOut, timedOut.Status);
        Assert.True(timedOut.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ProcessRunner_PowerShellVariantsReceiveInputAndEnvironment(string shell)
    {
        var runner = new ScriptProcessRunner();
        var result = await runner.RunAsync(
            new ScriptEntry
            {
                Name = shell,
                Shell = shell,
                Command = "$text = [Console]::In.ReadToEnd(); [Console]::Out.Write($env:TYPEWHISPER_LANGUAGE + '|' + $text)",
                TimeoutSeconds = 30
            },
            "hello",
            new PostProcessingContext { SourceLanguage = "de" },
            CancellationToken.None);

        if (shell == "pwsh" && result.Status == ScriptExecutionStatus.StartFailed)
        {
            Assert.Contains("pwsh", result.Error, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(ScriptExecutionStatus.Success, result.Status);
        Assert.Equal("de|hello", result.Output);
    }

    [Fact]
    public async Task ProcessRunner_StopsWhenOutputLimitIsExceeded()
    {
        var runner = new ScriptProcessRunner();
        var result = await runner.RunAsync(
            new ScriptEntry
            {
                Name = "large output",
                Shell = "powershell",
                Command = "[Console]::Out.Write(('x' * 1100000))"
            },
            "input",
            new PostProcessingContext(),
            CancellationToken.None);

        Assert.Equal(ScriptExecutionStatus.OutputLimitExceeded, result.Status);
        Assert.Contains("stdout", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunner_CallerCancellationAbortsExecution()
    {
        var runner = new ScriptProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ScriptEntry { Name = "cancel", Command = "ping 127.0.0.1 -n 20 >nul", TimeoutSeconds = 20 },
            "input",
            new PostProcessingContext(),
            cancellation.Token));
    }

    [Fact]
    public void LocalizationFiles_HaveMatchingKeysAndArePackagedByProject()
    {
        var localizationDirectory = Path.GetDirectoryName(
            FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "Localization", "en.json"))!;
        var files = new[] { "en.json", "de.json", "ja.json", "ru.json", "zh-Hans.json" };
        var expected = ReadKeys(Path.Combine(localizationDirectory, files[0]));

        foreach (var file in files)
            Assert.Equal(expected, ReadKeys(Path.Combine(localizationDirectory, file)));

        var project = File.ReadAllText(Path.Combine(localizationDirectory, "..", "TypeWhisper.Plugin.Script.csproj"));
        Assert.Contains("Localization\\*.json", project, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsXaml_DoesNotUseRunTextBindings()
    {
        var xaml = File.ReadAllText(
            FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "ScriptSettingsView.xaml"));

        Assert.DoesNotContain("<Run", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsXaml_UsesListOnlyLayoutWithoutEmbeddedEditor()
    {
        var xaml = File.ReadAllText(
            FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "ScriptSettingsView.xaml"));

        Assert.DoesNotContain("EditPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TestInputBox", xaml, StringComparison.Ordinal);
        Assert.Contains("EmptyStatePanel", xaml, StringComparison.Ordinal);
        Assert.Contains("ScriptList", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowDrop=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Drop=\"OnScriptListDrop\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DragLeave=\"OnScriptListDragLeave\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowDropBefore", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowDropAfter", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorXaml_KeepsFooterOutsideScrollableContent()
    {
        var xaml = File.ReadAllText(
            FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "ScriptEditorWindow.xaml"));

        Assert.Contains("Width=\"660\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"640\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"520\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"440\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorScrollViewer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorFooter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TestRunnerExpander\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationXaml_UsesFixedDarkLayoutWithVisibleActions()
    {
        var xaml = File.ReadAllText(
            FindRepositoryFile("plugins", "TypeWhisper.Plugin.Script", "ScriptConfirmationWindow.xaml"));

        Assert.Contains("Width=\"500\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"220\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeToContent=\"Manual\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ActionFooter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PrimaryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SecondaryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewKeyDown=\"OnPreviewKeyDown\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeToContent=\"WidthAndHeight\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_EmptyStateAndScriptListAreMutuallyExclusive()
    {
        RunInSta(() =>
        {
            using var emptyFixture = new ScriptFixture();
            var emptyView = new ScriptSettingsView(emptyFixture.Service);
            MeasureElement(emptyView, 600, 430);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(emptyView.FindName("EmptyStatePanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(emptyView.FindName("ListPanel")).Visibility);

            using var populatedFixture = new ScriptFixture(
                new ScriptEntry { Name = "Configured", Command = "more" });
            var populatedView = new ScriptSettingsView(populatedFixture.Service);
            MeasureElement(populatedView, 600, 430);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(populatedView.FindName("EmptyStatePanel")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(populatedView.FindName("ListPanel")).Visibility);
        });
    }

    [Theory]
    [InlineData(660, 640)]
    [InlineData(520, 440)]
    public void EditorWindow_FooterButtonsStayInsideVisibleBounds(double width, double height)
    {
        RunInSta(() =>
        {
            using var fixture = new ScriptFixture(
                new ScriptEntry { Name = "Configured", Command = "more" });
            var window = new ScriptEditorWindow(fixture.Service, fixture.Service.Scripts[0]);
            MeasureWindow(window, width, height);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var footer = Assert.IsType<Border>(window.FindName("EditorFooter"));
            var scroll = Assert.IsType<ScrollViewer>(window.FindName("EditorScrollViewer"));
            var save = Assert.IsType<Button>(window.FindName("SaveButton"));
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));

            Assert.False(scroll.IsAncestorOf(footer));
            AssertInside(root, save);
            AssertInside(root, cancel);
            var footerTop = BoundsWithin(root, footer).Top;

            Assert.IsType<Expander>(window.FindName("TestRunnerExpander")).IsExpanded = true;
            MeasureWindow(window, width, height);

            Assert.Equal(footerTop, BoundsWithin(root, footer).Top, precision: 3);
            AssertInside(root, save);
            AssertInside(root, cancel);
            window.ViewModel.Dispose();
        });
    }

    [Fact]
    public void ConfirmationWindow_FixedLayoutKeepsEveryUnsavedActionVisible()
    {
        RunInSta(() =>
        {
            var window = ScriptConfirmationWindow.CreateUnsaved(
                "Unsaved changes",
                "Save changes before closing?",
                "Save",
                "Discard",
                "Cancel",
                "Close");
            MeasureWindow(window, 500, 220);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            foreach (var name in new[] { "PrimaryButton", "SecondaryButton", "CancelButton" })
            {
                var button = Assert.IsType<Button>(window.FindName(name));
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(button.Content?.ToString()));
                AssertInside(root, button);
            }
        });
    }

    [Fact]
    public void EditorWindow_ShellSelectorUsesInteractiveReadableTemplate()
    {
        RunInSta(() =>
        {
            using var fixture = new ScriptFixture();
            var window = new ScriptEditorWindow(fixture.Service, null);
            MeasureWindow(window, 660, 640);
            var shell = Assert.IsType<ComboBox>(window.FindName("ShellCombo"));
            shell.ApplyTemplate();

            Assert.True(shell.IsEnabled);
            Assert.True(shell.Focusable);
            Assert.Equal(3, shell.Items.Count);
            Assert.NotNull(shell.ItemContainerStyle);
            Assert.IsType<ToggleButton>(shell.Template.FindName("DropDownToggle", shell));
            var selection = Assert.IsType<Border>(shell.Template.FindName("SelectionBorder", shell));
            var arrow = Assert.IsAssignableFrom<FrameworkElement>(shell.Template.FindName("DropDownArrow", shell));
            Assert.NotNull(selection.Background);
            Assert.True(selection.ActualWidth > 300, $"Shell field width was only {selection.ActualWidth}.");
            Assert.True(BoundsWithin(selection, arrow).Left > selection.ActualWidth - 30);

            shell.SelectedItem = "pwsh";
            Assert.Equal("pwsh", window.ViewModel.Shell);
            window.ViewModel.Dispose();
        });
    }

    private static SortedSet<string> ReadKeys(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return new SortedSet<string>(json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"typewhisper-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(action));
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test did not finish.");
        Assert.Null(failure);
    }

    private static void MeasureWindow(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        MeasureElement(Assert.IsAssignableFrom<FrameworkElement>(window.Content), width, height);
    }

    private static void MeasureElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static Rect BoundsWithin(FrameworkElement root, FrameworkElement element) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static void AssertInside(FrameworkElement root, FrameworkElement element)
    {
        var bounds = BoundsWithin(root, element);
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0, $"{element.Name} begins outside the window: {bounds}.");
        Assert.True(bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight,
            $"{element.Name} extends outside the window: {bounds} in {root.ActualWidth}x{root.ActualHeight}.");
    }

    private sealed class ScriptFixture : IDisposable
    {
        private readonly string _directory = CreateTemporaryDirectory();
        private readonly Mock<IPluginHostServices> _host = new();

        internal ScriptFixture(params ScriptEntry[] scripts)
        {
            Store = new MemoryStore(scripts);
            Runner = new FakeRunner();
            Dialogs = new FakeDialogs();
            EditorHost = new FakeEditorHost();
            _host.SetupGet(host => host.PluginDataDirectory).Returns(_directory);
            _host.SetupGet(host => host.Localization).Returns(new TestLocalization());
            Service = new ScriptService(_host.Object, Store, Runner);
        }

        internal MemoryStore Store { get; }
        internal FakeRunner Runner { get; }
        internal FakeDialogs Dialogs { get; }
        internal FakeEditorHost EditorHost { get; }
        internal ScriptService Service { get; }

        internal ScriptSettingsViewModel CreateViewModel() => new(Service, EditorHost, Dialogs);
        internal ScriptEditorViewModel CreateEditor(ScriptEntry? script) => new(Service, script, Dialogs);

        public void Dispose()
        {
            Service.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class MemoryStore(IEnumerable<ScriptEntry> scripts) : IScriptConfigurationStore
    {
        internal List<ScriptEntry> Scripts { get; private set; } = scripts.ToList();
        internal int SaveCount { get; private set; }

        public ScriptConfigurationLoadResult Load() => new(Scripts.ToList());

        public void Save(IReadOnlyCollection<ScriptEntry> scripts)
        {
            Scripts = scripts.ToList();
            SaveCount++;
        }
    }

    private sealed class ErrorStore(string error, IEnumerable<ScriptEntry>? scripts = null) : IScriptConfigurationStore
    {
        public ScriptConfigurationLoadResult Load() => new(scripts?.ToList() ?? [], error);
        public void Save(IReadOnlyCollection<ScriptEntry> scripts) => throw new InvalidOperationException();
    }

    private sealed class FakeRunner : IScriptProcessRunner
    {
        internal Queue<ScriptExecutionResult> Results { get; } = new();
        internal List<string> Inputs { get; } = [];
        internal ScriptExecutionResult? NextResult { get; set; }
        internal Func<ScriptEntry, string, CancellationToken, Task<ScriptExecutionResult>>? Handler { get; set; }
        internal ScriptEntry? LastScript { get; private set; }
        internal string? LastInput { get; private set; }

        public Task<ScriptExecutionResult> RunAsync(
            ScriptEntry script,
            string input,
            PostProcessingContext context,
            CancellationToken cancellationToken)
        {
            LastScript = script;
            LastInput = input;
            Inputs.Add(input);
            if (Handler is not null)
                return Handler(script, input, cancellationToken);
            return Task.FromResult(
                Results.Count > 0
                    ? Results.Dequeue()
                    : NextResult ?? new ScriptExecutionResult(
                        ScriptExecutionStatus.Success, input, "", 0, TimeSpan.Zero));
        }
    }

    private sealed class FakeEditorHost : IScriptEditorHost
    {
        internal ScriptEntry? LastScript { get; private set; }
        internal Func<ScriptEntry?, Guid?>? Handler { get; set; }

        public Guid? ShowEditor(ScriptEntry? script)
        {
            LastScript = script;
            return Handler?.Invoke(script);
        }
    }

    private sealed class FakeDialogs : IScriptConfirmationService
    {
        internal ConfirmationChoice UnsavedChoice { get; set; } = ConfirmationChoice.Secondary;
        internal bool RemoveConfirmed { get; set; } = true;
        public ConfirmationChoice ConfirmUnsavedChanges(string scriptName) => UnsavedChoice;
        public bool ConfirmRemove(string scriptName) => RemoveConfirmed;
    }

    private sealed class TestLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? key : $"{key}: {string.Join(", ", args)}";
    }
}
