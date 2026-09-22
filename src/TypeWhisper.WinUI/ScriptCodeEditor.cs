using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace TypeWhisper.WinUI;

/// <summary>A local plain-text script editor with lexical colors and text-only undo.</summary>
internal sealed class ScriptCodeEditor : RichEditBox
{
    private readonly ScriptEditHistory _history;
    private readonly int _maximum;
    private bool _updating;
    private bool _composing;
    private string _language;
    private string _text;
    internal event Action? SyntaxLanguageChanged;
    internal event Action<string>? CodeChanged;
    internal event Action<string>? EditorNotice;
    internal event Action<int, int>? PositionChanged;
    internal string Code => _text;
    internal string SyntaxLanguage { get => _language; set { _language = value; Highlight(); SyntaxLanguageChanged?.Invoke(); } }

    internal ScriptCodeEditor(string text, string language, int maximum, bool readOnly = false)
    {
        _maximum = Math.Clamp(maximum, 1, 32768);
        _language = language;
        _text = Normalize(text);
        _history = new(_text);
        FontFamily = new FontFamily("Cascadia Mono, Consolas");
        FontSize = 14;
        Height = readOnly ? 300 : 240;
        Padding = new Thickness(12);
        BorderThickness = new Thickness(0);
        Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        Foreground = (Brush)Application.Current.Resources["TextBrush"];
        TextWrapping = TextWrapping.NoWrap;
        IsSpellCheckEnabled = false;
        IsTextPredictionEnabled = false;
        IsReadOnly = readOnly;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        Document.SetText(TextSetOptions.None, _text);
        Document.UndoLimit = 0; // Formatting is deliberately excluded; see ScriptEditHistory.
        TextChanged += (_, _) => OnTextChanged();
        SelectionChanged += (_, _) => ReportPosition();
        TextCompositionStarted += (_, _) => _composing = true;
        TextCompositionEnded += (_, _) => { _composing = false; OnTextChanged(); Highlight(); };
        AddHandler(KeyDownEvent, new KeyEventHandler(OnEditorKeyDown), true);
        Paste += OnPaste;
        Loaded += (_, _) => Highlight();
        ActualThemeChanged += (_, _) => Highlight();
        ContextFlyout = CreateContextMenu();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private string ReadText()
    {
        // A range with None excludes the undeletable final paragraph marker.
        Document.GetRange(0, int.MaxValue).GetText(TextGetOptions.None, out var text);
        return Normalize(text);
    }

    private void OnTextChanged()
    {
        if (_updating || _composing) return;
        var text = ReadText();
        if (text == _text) return;
        if (text.Length > _maximum && text.Length >= _text.Length) { ReplaceText(_text); EditorNotice?.Invoke($"The command is limited to {_maximum:N0} characters."); return; }
        _text = text;
        _history.Record(text);
        Highlight();
        CodeChanged?.Invoke(text);
        ReportPosition();
    }

    private void ReplaceText(string text)
    {
        var caret = Math.Min(Document.Selection.StartPosition, text.Length);
        _updating = true;
        try { Document.SetText(TextSetOptions.None, text); Document.Selection.SetRange(caret, caret); _text = text; }
        finally { _updating = false; }
        Highlight(); CodeChanged?.Invoke(text); ReportPosition();
    }

    private void Highlight()
    {
        if (_updating || _composing || !IsLoaded) return;
        _updating = true;
        Document.BatchDisplayUpdates();
        try
        {
            var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var plain = highContrast ? new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)
                : ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 16, 24, 32) : Color.FromArgb(255, 244, 247, 250);
            Foreground = new SolidColorBrush(plain);
            Document.GetRange(0, int.MaxValue).CharacterFormat.ForegroundColor = plain;
            if (highContrast) return;
            foreach (var token in ScriptSyntax.Tokenize(_text, _language))
                Document.GetRange(token.Start, token.Start + token.Length).CharacterFormat.ForegroundColor = TokenColor(token.Kind);
        }
        finally { Document.ApplyDisplayUpdates(); _updating = false; }
    }

    private Color TokenColor(ScriptTokenKind kind)
    {
        var dark = ActualTheme != ElementTheme.Light;
        var rgb = kind switch
        {
            ScriptTokenKind.Keyword => dark ? 0x68B8FF : 0x005A9E,
            ScriptTokenKind.Variable => dark ? 0x7DD3FC : 0x075985,
            ScriptTokenKind.String => dark ? 0xA3D6A2 : 0x27632A,
            ScriptTokenKind.Type => dark ? 0xD6B5FF : 0x65409A,
            ScriptTokenKind.Number => dark ? 0xF1C77B : 0x875400,
            _ => dark ? 0xA7B5C5 : 0x526577
        };
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void ReportPosition()
    {
        var offset = Math.Clamp(Document.Selection.StartPosition, 0, _text.Length);
        var before = _text.AsSpan(0, offset);
        var line = 1;
        foreach (var character in before) if (character == '\n') line++;
        var lastBreak = before.LastIndexOf('\n');
        PositionChanged?.Invoke(line, offset - lastBreak);
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsReadOnly || _composing) return;
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!control || alt) return; // AltGr produces Ctrl+Alt on several keyboard layouts.
        if (e.Key is VirtualKey.B or VirtualKey.I or VirtualKey.U) { e.Handled = true; return; }
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (e.Key == VirtualKey.Z) { e.Handled = true; if (shift) RedoText(); else UndoText(); }
        else if (e.Key == VirtualKey.Y) { e.Handled = true; RedoText(); }
    }

    internal void FormatCode()
    {
        if (_language is not ("powershell" or "pwsh")) return;
        var formatted = ScriptSyntax.FormatPowerShell(_text);
        if ((formatted.Length > _maximum && formatted.Length >= _text.Length) || formatted == _text) return;
        _history.Record(formatted); ReplaceText(formatted);
    }

    private void UndoText() { if (_history.CanUndo) ReplaceText(_history.Undo()); }
    private void RedoText() { if (_history.CanRedo) ReplaceText(_history.Redo()); }
    private async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        await PastePlainTextAsync();
    }

    private async Task PastePlainTextAsync()
    {
        if (IsReadOnly) return;
        var start = Document.Selection.StartPosition;
        var end = Document.Selection.EndPosition;
        var original = _text;
        try
        {
            var clipboard = Clipboard.GetContent();
            if (!clipboard.Contains(StandardDataFormats.Text)) return;
            var pasted = Normalize(await clipboard.GetTextAsync());
            if (!IsLoaded || _text != original || Document.Selection.StartPosition != start || Document.Selection.EndPosition != end) return;
            if (_text.Length - (end - start) + pasted.Length > _maximum && pasted.Length >= end - start) { EditorNotice?.Invoke($"The command is limited to {_maximum:N0} characters."); return; }
            Document.Selection.SetText(TextSetOptions.None, pasted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { EditorNotice?.Invoke("The clipboard is unavailable. Try again."); }
    }

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();
        void Add(string title, Action action) { var item = new MenuFlyoutItem { Text = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        if (!IsReadOnly) { Add("Undo", UndoText); Add("Redo", RedoText); menu.Items.Add(new MenuFlyoutSeparator()); }
        Add("Copy", () => Document.Selection.Copy());
        if (!IsReadOnly)
        {
            Add("Cut", () => { Document.Selection.Copy(); Document.Selection.SetText(TextSetOptions.None, ""); });
            Add("Paste", () => _ = PastePlainTextAsync());
        }
        Add("Select all", () => Document.Selection.SetRange(0, _text.Length));
        return menu;
    }
}
