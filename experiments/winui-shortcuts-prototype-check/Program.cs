using TypeWhisper.WinUIPrototype;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}"); count++;
}
Check(PrototypeShortcutRules.Normalize(" shift + control + k ") == "CTRL+SHIFT+K", "Canonical modifier order and aliases");
Check(PrototypeShortcutRules.Validate("Ctrl+Shift", true) is null, "Hold supports two modifiers");
Check(PrototypeShortcutRules.Validate("Ctrl+Shift", false) is not null, "Optional key-required validation policy");
Check(PrototypeShortcutRules.Validate("Ctrl+Ctrl", true) is not null, "Repeated modifiers rejected");
Check(PrototypeShortcutRules.Validate("K", false) is not null, "Plain letter rejected");
Check(PrototypeShortcutRules.Validate("F12", false) is null, "Function key accepted");
Check(PrototypeShortcutRules.Validate("Alt+F4", false) is not null, "Reserved Windows chord rejected");
Check(PrototypeShortcutRules.Validate("Win+K", false) is null, "Windows-key chord accepted");
Check(PrototypeShortcutRules.Validate("Shift+Win", true) is null, "Requested Shift + Win combination accepted");
Check(PrototypeShortcutRules.Validate("Win+Alt", true) is null, "Windows-app modifier-only regression");
Check(PrototypeShortcutRules.Validate("Ctrl+Shift+Win", true) is null, "Three-modifier chord accepted");
Check(PrototypeShortcutRules.Validate("Win", true) is not null, "Single generic Windows modifier remains invalid");
Check(PrototypeShortcutRules.Duplicate("Win+Shift", ["Shift+Win"], -1) is not null, "Reordered Windows chord duplicate detected");
Check(PrototypeShortcutRules.Conflict("Win+Shift", "recorder", [("main", "Main dictation", "Ctrl+F9,Shift+Win")]) is not null, "Windows modifier chord conflicts across actions");
Check(PrototypeShortcutRules.Validate("Ctrl+Alt+K", false) is null, "Normal chord accepted");
Check(PrototypeShortcutRules.Validate("", false) is not null, "Empty capture rejected");
(string Key, string Label, string Value)[] bindings = [("main", "Main dictation", "Ctrl+Shift+F9,Ctrl+Alt+K"), ("hold", "Hold", "Ctrl+Shift")];
Check(PrototypeShortcutRules.Conflict("Alt+Control+K", "toggle", bindings)?.Contains("Main dictation") == true, "Conflict across comma-separated bindings");
Check(PrototypeShortcutRules.Conflict("Ctrl+Alt+K", "main", bindings) is null, "Own binding is not a conflict");
Check(PrototypeShortcutRules.Conflict("", "main", bindings) is null, "Unassigned binding allowed");
var alternatives = PrototypeShortcutRules.Split("Ctrl+Shift+F9, Ctrl+Alt+K");
Check(alternatives.Length == 2, "Existing alternative bindings loaded individually");
var added = PrototypeShortcutRules.Upsert(alternatives, -1, "Ctrl+Alt+J");
Check(added == "Ctrl+Shift+F9,Ctrl+Alt+K,Ctrl+Alt+J", "Adding preserves all existing bindings");
Check(PrototypeShortcutRules.Upsert(alternatives, 1, "Ctrl+Alt+L") == "Ctrl+Shift+F9,Ctrl+Alt+L", "Editing replaces only the chosen binding");
Check(PrototypeShortcutRules.RemoveAt(alternatives, 0) == "Ctrl+Alt+K", "Removing preserves other bindings");
Check(PrototypeShortcutRules.RemoveAt(["F9"], 0) == "", "Removing the last binding leaves action unassigned");
Check(PrototypeShortcutRules.Duplicate("Alt+Control+K", alternatives, -1) is not null, "Duplicate within same action blocked");
Check(PrototypeShortcutRules.Duplicate("Ctrl+Alt+K", alternatives, 1) is null, "Keeping edited binding unchanged allowed");
Check(PrototypeShortcutRules.Duplicate("Ctrl+Shift+F9", alternatives, 1) is not null, "Edit cannot duplicate a sibling binding");
Check(PrototypeShortcutRules.Split("").Length == 0, "Empty action has no phantom binding");
Console.WriteLine($"{count} checks passed.");
