using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

public partial class SnippetsViewModel : ObservableObject
{
    private readonly ISnippetService _snippets;

    [ObservableProperty] private Snippet? _selectedSnippet;

    // Editor state (bound by SnippetEditorWindow)
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isCreatingNew;
    [ObservableProperty] private string _editTrigger = "";
    [ObservableProperty] private string _editReplacement = "";
    [ObservableProperty] private bool _editCaseSensitive;
    [ObservableProperty] private string _editTags = "";
    private string? _editingSnippetId;

    // Tag filter
    [ObservableProperty] private string? _selectedTagFilter;
    public ObservableCollection<string> AvailableTags { get; } = [];
    public ObservableCollection<Snippet> Snippets { get; } = [];

    public SnippetsViewModel(ISnippetService snippets)
    {
        _snippets = snippets;
        _snippets.SnippetsChanged += RefreshSnippets;
        RefreshSnippets();
    }

    partial void OnSelectedTagFilterChanged(string? value) => RefreshSnippets();

    [RelayCommand]
    private void StartCreate()
    {
        _editingSnippetId = null;
        IsCreatingNew = true;
        EditTrigger = "";
        EditReplacement = "";
        EditCaseSensitive = false;
        EditTags = "";
        IsEditorOpen = true;
        ShowEditorDialog();
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (SelectedSnippet is null) return;
        _editingSnippetId = SelectedSnippet.Id;
        IsCreatingNew = false;
        EditTrigger = SelectedSnippet.Trigger;
        EditReplacement = SelectedSnippet.Replacement;
        EditCaseSensitive = SelectedSnippet.CaseSensitive;
        EditTags = SelectedSnippet.Tags;
        IsEditorOpen = true;
        ShowEditorDialog();
    }

    private void ShowEditorDialog()
    {
        var owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        var editor = new SnippetEditorWindow(this);
        if (owner is not null) editor.Owner = owner;
        editor.ShowDialog();
    }

    [RelayCommand]
    private void SaveEditor()
    {
        if (string.IsNullOrWhiteSpace(EditTrigger) || string.IsNullOrWhiteSpace(EditReplacement)) return;

        if (IsCreatingNew)
        {
            _snippets.AddSnippet(new Snippet
            {
                Id = Guid.NewGuid().ToString(),
                Trigger = EditTrigger.Trim(),
                Replacement = EditReplacement,
                CaseSensitive = EditCaseSensitive,
                Tags = EditTags.Trim()
            });
        }
        else if (_editingSnippetId is not null)
        {
            var existing = _snippets.Snippets.FirstOrDefault(s => s.Id == _editingSnippetId);
            if (existing is not null)
            {
                _snippets.UpdateSnippet(existing with
                {
                    Trigger = EditTrigger.Trim(),
                    Replacement = EditReplacement,
                    CaseSensitive = EditCaseSensitive,
                    Tags = EditTags.Trim()
                });
            }
        }

        IsEditorOpen = false;
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;

    [RelayCommand]
    private void DeleteSnippet()
    {
        if (SelectedSnippet is null) return;

        var result = MessageBox.Show(
            $"Snippet \"{SelectedSnippet.Trigger}\" wirklich löschen?",
            "Snippet löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _snippets.DeleteSnippet(SelectedSnippet.Id);
        SelectedSnippet = null;
    }

    [RelayCommand]
    private void ToggleEnabled()
    {
        if (SelectedSnippet is null) return;
        _snippets.UpdateSnippet(SelectedSnippet with { IsEnabled = !SelectedSnippet.IsEnabled });
    }

    [RelayCommand]
    private void ToggleEnabledItem(Snippet? snippet)
    {
        if (snippet is null) return;
        _snippets.UpdateSnippet(snippet with { IsEnabled = !snippet.IsEnabled });
    }

    [RelayCommand]
    private void EditItem(Snippet? snippet)
    {
        if (snippet is null) return;
        SelectedSnippet = snippet;
        StartEdit();
    }

    [RelayCommand]
    private void DeleteItem(Snippet? snippet)
    {
        if (snippet is null) return;
        SelectedSnippet = snippet;
        DeleteSnippet();
    }

    [RelayCommand]
    private void ClearTagFilter() => SelectedTagFilter = null;

    [RelayCommand]
    private void InsertPlaceholder(string placeholder)
    {
        EditReplacement += placeholder;
    }

    [RelayCommand]
    private void ExportSnippets()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON Dateien (*.json)|*.json",
            FileName = "snippets.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() != true) return;

        var json = _snippets.ExportToJson();
        File.WriteAllText(dialog.FileName, json);
    }

    [RelayCommand]
    private void ImportSnippets()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON Dateien (*.json)|*.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() != true) return;

        var json = File.ReadAllText(dialog.FileName);
        var count = _snippets.ImportFromJson(json);
        MessageBox.Show($"{count} Snippet(s) importiert.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RefreshSnippets()
    {
        Snippets.Clear();
        foreach (var s in _snippets.Snippets)
        {
            if (SelectedTagFilter is not null)
            {
                var tags = s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!tags.Contains(SelectedTagFilter, StringComparer.OrdinalIgnoreCase))
                    continue;
            }
            Snippets.Add(s);
        }

        AvailableTags.Clear();
        foreach (var tag in _snippets.AllTags)
            AvailableTags.Add(tag);
    }
}
