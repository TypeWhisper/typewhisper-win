using System.Globalization;
using System.Windows.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Converters;

/// <summary>
/// Provides equality to string converter behavior.
/// </summary>
public sealed class EqualityToStringConverter : IMultiValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]) ? "True" : "False";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, Binding.DoNothing);
        return values;
    }
}

/// <summary>
/// Provides workflow trigger summary converter behavior.
/// </summary>
public sealed class WorkflowTriggerSummaryConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Workflow workflow ? WorkflowsViewModel.WorkflowTriggerSummary(workflow) : "";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow trigger detail converter behavior.
/// </summary>
public sealed class WorkflowTriggerDetailConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Workflow workflow ? WorkflowsViewModel.WorkflowTriggerDetail(workflow) : "";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow template name converter behavior.
/// </summary>
public sealed class WorkflowTemplateNameConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WorkflowTemplate template
            ? WorkflowTemplateCatalog.DefinitionFor(template).Name
            : "";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow template icon converter behavior.
/// </summary>
public sealed class WorkflowTemplateIconConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WorkflowTemplate template ? WorkflowsViewModel.TemplateIconGlyph(template) : "";

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow trigger icon converter behavior.
/// </summary>
public sealed class WorkflowTriggerIconConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value switch
        {
            Workflow workflow => workflow.Trigger.Kind,
            WorkflowTrigger trigger => trigger.Kind,
            WorkflowTriggerKind triggerKind => triggerKind,
            _ => (WorkflowTriggerKind?)null
        };

        return kind is { } triggerKindValue
            ? WorkflowsViewModel.TriggerIconGlyph(triggerKindValue)
            : "";
    }

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow enabled label converter behavior.
/// </summary>
public sealed class WorkflowEnabledLabelConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool enabled && enabled
            ? Loc.Instance["Workflows.Enabled"]
            : Loc.Instance["Workflows.Disabled"];

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Provides workflow toggle label converter behavior.
/// </summary>
public sealed class WorkflowToggleLabelConverter : IValueConverter
{
    /// <summary>
    /// Converts a source value for WPF binding.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool enabled && enabled
            ? Loc.Instance["Workflows.Disable"]
            : Loc.Instance["Workflows.Enable"];

    /// <summary>
    /// Converts a WPF binding value back to the source representation.
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
