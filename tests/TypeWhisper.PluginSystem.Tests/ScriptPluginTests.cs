namespace TypeWhisper.PluginSystem.Tests;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Moq;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;

public sealed class ScriptPluginTests
{
    [Fact]
    public void SettingsView_StagesEditsUntilSaveAndSupportsCancel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dataDirectory = Path.Join(Path.GetTempPath(), $"typewhisper-script-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);

            try
            {
                var host = new Mock<IPluginHostServices>();
                host.SetupGet(x => x.PluginDataDirectory).Returns(dataDirectory);

                var plugin = new ScriptPlugin();
                plugin.ActivateAsync(host.Object).GetAwaiter().GetResult();

                var original = new ScriptEntry { Name = "Original", Command = "echo original" };
                var other = new ScriptEntry { Name = "Other" };
                plugin.Service!.AddScript(original);
                plugin.Service.AddScript(other);

                var view = Assert.IsType<ScriptSettingsView>(plugin.CreateSettingsView());
                var list = Assert.IsType<ListBox>(view.FindName("ScriptList"));
                var panel = Assert.IsType<Border>(view.FindName("EditPanel"));
                var name = Assert.IsType<TextBox>(view.FindName("NameBox"));
                var command = Assert.IsType<TextBox>(view.FindName("CommandBox"));
                var shell = Assert.IsType<ComboBox>(view.FindName("ShellCombo"));
                var save = Assert.IsType<Button>(view.FindName("SaveButton"));
                var cancel = Assert.IsType<Button>(view.FindName("CancelButton"));

                list.SelectedItem = original;
                name.Text = "Edited Script";
                command.Text = "echo edited";
                shell.SelectedIndex = 1;

                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Same(original, list.SelectedItem);
                Assert.Equal("Original", plugin.Service.Scripts[0].Name);
                Assert.True(save.IsEnabled);
                Assert.True(cancel.IsEnabled);

                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var saved = plugin.Service.Scripts[0];
                Assert.Equal("Edited Script", saved.Name);
                Assert.Equal("echo edited", saved.Command);
                Assert.Equal("powershell", saved.Shell);
                Assert.Same(saved, list.SelectedItem);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.False(save.IsEnabled);
                Assert.False(cancel.IsEnabled);

                var reloaded = new ScriptService(host.Object);
                Assert.Equal(saved, reloaded.Scripts.Single(script => script.Id == saved.Id));

                name.Text = "Discarded";
                Assert.True(save.IsEnabled);
                Assert.True(cancel.IsEnabled);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("Edited Script", name.Text);
                Assert.Equal("Edited Script", plugin.Service.Scripts[0].Name);
                Assert.False(save.IsEnabled);
                Assert.False(cancel.IsEnabled);

                name.Text = "Also discarded";
                list.SelectedItem = other;
                list.SelectedItem = saved;
                Assert.Equal("Edited Script", name.Text);
                Assert.Equal(Visibility.Visible, panel.Visibility);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try { Directory.Delete(dataDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Script settings test did not finish.");
        Assert.Null(failure);
    }
}
