using TypeWhisper.WinUI;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}"); count++;
}
Check(ShortcutRules.Normalize(" shift + control + k ") == "CTRL+SHIFT+K", "Canonical modifier order and aliases");
Check(ShortcutRules.Validate("Ctrl+Shift", true) is null, "Hold supports two modifiers");
Check(ShortcutRules.Validate("Ctrl+Shift", false) is not null, "Optional key-required validation policy");
Check(ShortcutRules.Validate("Ctrl+Ctrl", true) is not null, "Repeated modifiers rejected");
Check(ShortcutRules.Validate("K", false) is not null, "Plain letter rejected");
Check(ShortcutRules.Validate("F12", false) is null, "Function key accepted");
Check(ShortcutRules.Validate("Alt+F4", false) is not null, "Reserved Windows chord rejected");
Check(ShortcutRules.Validate("Win+K", false) is null, "Windows-key chord accepted");
Check(ShortcutRules.Validate("Shift+Win", true) is null, "Requested Shift + Win combination accepted");
Check(ShortcutRules.Validate("Win+Alt", true) is null, "Windows-app modifier-only regression");
Check(ShortcutRules.Validate("Ctrl+Shift+Win", true) is null, "Three-modifier chord accepted");
Check(ShortcutRules.Validate("Win", true) is not null, "Single generic Windows modifier remains invalid");
Check(ShortcutRules.Duplicate("Win+Shift", ["Shift+Win"], -1) is not null, "Reordered Windows chord duplicate detected");
Check(ShortcutRules.Conflict("Win+Shift", "recorder", [("main", "Main dictation", "Ctrl+F9,Shift+Win")]) is not null, "Windows modifier chord conflicts across actions");
Check(ShortcutRules.Validate("Ctrl+Alt+K", false) is null, "Normal chord accepted");
Check(ShortcutRules.Validate("", false) is not null, "Empty capture rejected");
(string Key, string Label, string Value)[] bindings = [("main", "Main dictation", "Ctrl+Shift+F9,Ctrl+Alt+K"), ("hold", "Hold", "Ctrl+Shift")];
Check(ShortcutRules.Conflict("Alt+Control+K", "toggle", bindings)?.Contains("Main dictation") == true, "Conflict across comma-separated bindings");
Check(ShortcutRules.Conflict("Ctrl+Alt+K", "main", bindings) is null, "Own binding is not a conflict");
Check(ShortcutRules.Conflict("", "main", bindings) is null, "Unassigned binding allowed");
var alternatives = ShortcutRules.Split("Ctrl+Shift+F9, Ctrl+Alt+K");
Check(alternatives.Length == 2, "Existing alternative bindings loaded individually");
var added = ShortcutRules.Upsert(alternatives, -1, "Ctrl+Alt+J");
Check(added == "Ctrl+Shift+F9,Ctrl+Alt+K,Ctrl+Alt+J", "Adding preserves all existing bindings");
Check(ShortcutRules.Upsert(alternatives, 1, "Ctrl+Alt+L") == "Ctrl+Shift+F9,Ctrl+Alt+L", "Editing replaces only the chosen binding");
Check(ShortcutRules.RemoveAt(alternatives, 0) == "Ctrl+Alt+K", "Removing preserves other bindings");
Check(ShortcutRules.RemoveAt(["F9"], 0) == "", "Removing the last binding leaves action unassigned");
Check(ShortcutRules.Duplicate("Alt+Control+K", alternatives, -1) is not null, "Duplicate within same action blocked");
Check(ShortcutRules.Duplicate("Ctrl+Alt+K", alternatives, 1) is null, "Keeping edited binding unchanged allowed");
Check(ShortcutRules.Duplicate("Ctrl+Shift+F9", alternatives, 1) is not null, "Edit cannot duplicate a sibling binding");
Check(ShortcutRules.Split("").Length == 0, "Empty action has no phantom binding");
Console.WriteLine($"{count} checks passed.");
