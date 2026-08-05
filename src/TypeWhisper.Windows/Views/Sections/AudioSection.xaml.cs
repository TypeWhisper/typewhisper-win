using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides audio section behavior.
/// </summary>
public partial class AudioSection : UserControl
{
    private const string MicrophonePriorityItemDataFormat = "TypeWhisper.MicrophonePriorityItem";
    private Point? _microphonePriorityDragStartPoint;
    private MicrophonePriorityListItem? _draggedMicrophonePriorityItem;

    /// <summary>
    /// Initializes a new instance of the AudioSection class.
    /// </summary>
    public AudioSection() => InitializeComponent();

    private void Model_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        if (fe.DataContext is not ModelItemViewModel model)
            return;

        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm)
            return;

        if (vm.ModelManager.ActivateModelCommand.CanExecute(model.FullId))
            vm.ModelManager.ActivateModelCommand.Execute(model.FullId);
    }

    private void OnMicrophonePriorityDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MicrophonePriorityListItem item })
            return;

        _draggedMicrophonePriorityItem = item;
        _microphonePriorityDragStartPoint = e.GetPosition(this);
    }

    private void OnMicrophonePriorityDragHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not DependencyObject dragSource
            || _draggedMicrophonePriorityItem is null
            || _microphonePriorityDragStartPoint is not Point dragStartPoint)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        try
        {
            var data = new DataObject(MicrophonePriorityItemDataFormat, _draggedMicrophonePriorityItem);
            DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Move);
            e.Handled = true;
        }
        finally
        {
            _draggedMicrophonePriorityItem = null;
            _microphonePriorityDragStartPoint = null;
        }
    }

    private void OnMicrophonePriorityItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(MicrophonePriorityItemDataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnMicrophonePriorityItemDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MicrophonePriorityListItem target }
            || e.Data.GetData(MicrophonePriorityItemDataFormat) is not MicrophonePriorityListItem source
            || Equals(source, target)
            || DataContext is not SettingsWindowViewModel vm)
        {
            e.Handled = true;
            return;
        }

        var request = new MicrophonePriorityReorderRequest(source, target);
        if (vm.Settings.ReorderMicrophonePriorityItemCommand.CanExecute(request))
            vm.Settings.ReorderMicrophonePriorityItemCommand.Execute(request);

        e.Handled = true;
    }
}
