using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

internal sealed class MicrophonePriorityEditor : StackPanel
{
    private readonly LocalDictationSession _session;
    private readonly ObservableCollection<PriorityRow> _items = [];
    private readonly ListView _list;
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly PrototypeChoicePicker _add = new();
    internal PrototypeChoicePicker AddPicker => _add;

    internal MicrophonePriorityEditor(LocalDictationSession session)
    {
        _session = session; Spacing = 8;
        Children.Add(new TextBlock { Text = "Microphones", FontSize = 14 });
        _list = new ListView
        {
            ItemsSource = _items, CanDragItems = true, CanReorderItems = true, AllowDrop = true,
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = (Style)XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListViewItem">
                  <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                  <Setter Property="Padding" Value="0"/>
                  <Setter Property="Margin" Value="0,0,0,6"/>
                  <Setter Property="MinHeight" Value="42"/>
                  <Setter Property="CornerRadius" Value="8"/>
                  <Setter Property="UseSystemFocusVisuals" Value="True"/>
                  <Setter Property="Template">
                    <Setter.Value>
                      <ControlTemplate TargetType="ListViewItem">
                        <Grid>
                          <VisualStateManager.VisualStateGroups>
                            <VisualStateGroup x:Name="CommonStates">
                              <VisualState x:Name="Normal"/>
                              <VisualState x:Name="PointerOver"><VisualState.Setters><Setter Target="RowSurface.Background" Value="{ThemeResource ElevatedBrush}"/></VisualState.Setters></VisualState>
                              <VisualState x:Name="Pressed"><VisualState.Setters><Setter Target="RowSurface.BorderBrush" Value="{ThemeResource AccentBrush}"/></VisualState.Setters></VisualState>
                              <VisualState x:Name="Selected"><VisualState.Setters><Setter Target="RowSurface.BorderBrush" Value="{ThemeResource AccentBrush}"/></VisualState.Setters></VisualState>
                              <VisualState x:Name="PointerOverSelected"><VisualState.Setters><Setter Target="RowSurface.Background" Value="{ThemeResource ElevatedBrush}"/><Setter Target="RowSurface.BorderBrush" Value="{ThemeResource AccentBrush}"/></VisualState.Setters></VisualState>
                              <VisualState x:Name="PressedSelected"><VisualState.Setters><Setter Target="RowSurface.BorderBrush" Value="{ThemeResource FocusBrush}"/></VisualState.Setters></VisualState>
                              <VisualState x:Name="Disabled"><VisualState.Setters><Setter Target="RowSurface.Opacity" Value="0.5"/></VisualState.Setters></VisualState>
                            </VisualStateGroup>
                          </VisualStateManager.VisualStateGroups>
                          <Border x:Name="RowSurface" Background="{ThemeResource SurfaceBrush}" BorderBrush="{ThemeResource HairlineBrush}" BorderThickness="1" CornerRadius="8">
                            <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" HorizontalContentAlignment="Stretch"/>
                          </Border>
                        </Grid>
                      </ControlTemplate>
                    </Setter.Value>
                  </Setter>
                </Style>
                """),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="using:TypeWhisper.WinUI">
                  <Grid Padding="12,4,6,4" ColumnSpacing="10" MinHeight="42">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <TextBlock Text="≡" Width="20" TextAlignment="Center" VerticalAlignment="Center" Foreground="{StaticResource AccentBrush}" ToolTipService.ToolTip="Drag to reorder"/>
                    <TextBlock Grid.Column="1" Text="{Binding Name}" FontSize="13" TextTrimming="CharacterEllipsis" VerticalAlignment="Center">
                      <ToolTipService.ToolTip><ToolTip Content="{Binding Name}" Style="{StaticResource PrototypeHeatmapToolTipStyle}"/></ToolTipService.ToolTip>
                    </TextBlock>
                    <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="2">
                      <local:HandCursorButton Command="{Binding Up}" IsEnabled="{Binding CanMoveUp}" AutomationProperties.Name="Move microphone up" ToolTipService.ToolTip="Move up" Width="32" Height="32" Padding="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center" Style="{StaticResource PrototypeMenuButtonStyle}"><FontIcon Glyph="&#xE74A;" FontSize="12"/></local:HandCursorButton>
                      <local:HandCursorButton Command="{Binding Down}" IsEnabled="{Binding CanMoveDown}" AutomationProperties.Name="Move microphone down" ToolTipService.ToolTip="Move down" Width="32" Height="32" Padding="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center" Style="{StaticResource PrototypeMenuButtonStyle}"><FontIcon Glyph="&#xE74B;" FontSize="12"/></local:HandCursorButton>
                      <local:HandCursorButton Command="{Binding Remove}" AutomationProperties.Name="Remove microphone from priority list" ToolTipService.ToolTip="Remove" Width="32" Height="32" Padding="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center" Style="{StaticResource PrototypeMenuButtonStyle}"><FontIcon Glyph="&#xE711;" FontSize="12"/></local:HandCursorButton>
                    </StackPanel>
                  </Grid>
                </DataTemplate>
                """)
        };
        AutomationProperties.SetName(_list, "Microphone priority, highest priority first");
        _list.DragItemsStarting += (_, e) => { if (session.IsRecording) { e.Cancel = true; _hint.Text = "Finish recording before reordering microphones."; } };
        _list.DragItemsCompleted += (_, _) => Save();
        Children.Add(_list);
        _add.Configure("Add microphone", "microphone", "Add microphone to priority list");
        _add.SelectionChanged += id =>
        {
            var device = session.GetMicrophones().FirstOrDefault(item => item.Id == id);
            if (device is not null && !_items.Any(item => item.Item.Id == id))
            {
                var error = session.SetMicrophonePriority(_items.Select(item => item.Item).Append(new MicrophonePriorityItem(device.Id, device.Name)).ToArray());
                Refresh(); if (error is not null) _hint.Text = error;
            }
        };
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.Children.Add(_add);
        var refresh = new HandCursorButton { Content = new FontIcon { Glyph = "\uE72C", FontSize = 16 }, Width = 42, Height = 42,
            Padding = new Thickness(8), Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        AutomationProperties.SetName(refresh, "Refresh microphones"); ToolTipService.SetToolTip(refresh, "Refresh microphones");
        refresh.Click += (_, _) => Refresh(); Grid.SetColumn(refresh, 1); addRow.Children.Add(refresh);
        Children.Add(addRow); Children.Add(_hint);
        Refresh();
    }

    private void Move(PriorityRow item, int offset)
    {
        var index = _items.IndexOf(item);
        if (index < 0 || index + offset < 0 || index + offset >= _items.Count) return;
        _items.Move(index, index + offset); Save(); _list.SelectedIndex = index + offset;
    }

    private void Save()
    {
        var error = _session.SetMicrophonePriority(_items.Select(item => item.Item).ToArray());
        Refresh();
        if (error is not null) _hint.Text = error;
    }

    internal void Refresh()
    {
        _items.Clear(); foreach (var item in _session.MicrophonePriority)
        {
            var row = new PriorityRow(item, _items.Count > 0, _items.Count < _session.MicrophonePriority.Count - 1);
            row.Up = new ActionCommand(() => Move(row, -1)); row.Down = new ActionCommand(() => Move(row, 1));
            row.Remove = new ActionCommand(() => { _items.Remove(row); Save(); });
            _items.Add(row);
        }
        _list.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var devices = _session.GetMicrophones();
        _add.SetOptions(devices.Where(device => !_items.Any(item => item.Item.Id == device.Id))
            .Select(device => new PrototypeChoice(device.Id, device.Name, "Add to priority list")).ToArray(), "", _items.Count == 0 ? "System default · add microphone…" : "Add microphone…");
        var missing = _items.Where(item => !devices.Any(device => device.Id == item.Item.Id)).Select(item => item.Name).ToArray();
        _hint.Text = missing.Length > 0 ? "Disconnected (kept in priority list): " + string.Join(", ", missing)
            : _items.Count == 0 ? "Uses Windows default until you add a microphone." : "Drag to prioritize. First available wins; Windows default is the fallback.";
    }

    public sealed class PriorityRow(MicrophonePriorityItem item, bool canMoveUp, bool canMoveDown)
    {
        public MicrophonePriorityItem Item => item;
        public string Name => item.Name;
        public bool CanMoveUp => canMoveUp;
        public bool CanMoveDown => canMoveDown;
        public System.Windows.Input.ICommand Up { get; set; } = null!;
        public System.Windows.Input.ICommand Down { get; set; } = null!;
        public System.Windows.Input.ICommand Remove { get; set; } = null!;
    }
    private sealed class ActionCommand(Action action) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
