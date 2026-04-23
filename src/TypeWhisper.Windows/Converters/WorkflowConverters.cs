using System.Globalization;
using System.Windows.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Converters;

public sealed class EqualityToStringConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]) ? "True" : "False";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, Binding.DoNothing);
        return values;
    }
}

public sealed class WorkflowTriggerSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Workflow workflow ? WorkflowsViewModel.WorkflowTriggerSummary(workflow) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowTriggerDetailConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Workflow workflow ? WorkflowsViewModel.WorkflowTriggerDetail(workflow) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowTemplateNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WorkflowTemplate template
            ? WorkflowTemplateCatalog.DefinitionFor(template).Name
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowTemplateIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WorkflowTemplate template ? WorkflowsViewModel.TemplateIconGlyph(template) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowTriggerIconConverter : IValueConverter
{
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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowEnabledLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool enabled && enabled
            ? Loc.Instance["Workflows.Enabled"]
            : Loc.Instance["Workflows.Disabled"];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class WorkflowToggleLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool enabled && enabled
            ? Loc.Instance["Workflows.Disable"]
            : Loc.Instance["Workflows.Enable"];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
