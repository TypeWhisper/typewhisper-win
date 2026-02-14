using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

public partial class SnippetsViewModel : ObservableObject
{
    private readonly ISnippetService _snippets;

    [ObservableProperty] private Snippet? _selectedSnippet;
    [ObservableProperty] private string _newTrigger = "";
    [ObservableProperty] private string _newReplacement = "";

    public ObservableCollection<Snippet> Snippets { get; } = [];

    public SnippetsViewModel(ISnippetService snippets)
    {
        _snippets = snippets;
        _snippets.SnippetsChanged += RefreshSnippets;
        RefreshSnippets();
    }

    [RelayCommand]
    private void AddSnippet()
    {
        if (string.IsNullOrWhiteSpace(NewTrigger) || string.IsNullOrWhiteSpace(NewReplacement)) return;

        _snippets.AddSnippet(new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            Trigger = NewTrigger.Trim(),
            Replacement = NewReplacement.Trim()
        });

        NewTrigger = "";
        NewReplacement = "";
    }

    [RelayCommand]
    private void DeleteSnippet()
    {
        if (SelectedSnippet is null) return;
        _snippets.DeleteSnippet(SelectedSnippet.Id);
        SelectedSnippet = null;
    }

    [RelayCommand]
    private void ToggleEnabled()
    {
        if (SelectedSnippet is null) return;
        _snippets.UpdateSnippet(SelectedSnippet with { IsEnabled = !SelectedSnippet.IsEnabled });
    }

    private void RefreshSnippets()
    {
        Snippets.Clear();
        foreach (var s in _snippets.Snippets)
            Snippets.Add(s);
    }
}
