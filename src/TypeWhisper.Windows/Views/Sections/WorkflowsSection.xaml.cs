using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class WorkflowsSection : UserControl
{
    public WorkflowsSection() => InitializeComponent();

    private void WorkflowRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (sender is not FrameworkElement { DataContext: Workflow workflow })
            return;

        if (DataContext is not SettingsWindowViewModel vm || !vm.Workflows.StartEditCommand.CanExecute(workflow))
            return;

        vm.Workflows.StartEditCommand.Execute(workflow);
        e.Handled = true;
    }

    private void ProcessNameInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ViewModels.SettingsWindowViewModel vm)
            vm.Workflows.AddProcessNameChipCommand.Execute(null);
        e.Handled = true;
    }

    private void WebsitePatternInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is ViewModels.SettingsWindowViewModel vm)
            vm.Workflows.AddWebsitePatternChipCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
