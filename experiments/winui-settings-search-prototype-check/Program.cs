using TypeWhisper.WinUIPrototype;

PrototypeSettingSearchEntry[] entries =
[
    new("Recorder", "micDefault", "Microphone on by default", "Start new sessions with your microphone enabled.", "microphone"),
    new("Audio", "mic", "Microphone", "Choose your input device.", "microphone", "System default USB headset"),
    new("Audio", "quiet", "Whisper mode", "Boost quiet speech automatically.", "microphone"),
    new("Appearance", "overlay", "Recording overlay", "Choose an indicator.", "signal", "Standard Compact Minimal")
];
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}
Check(PrototypeSettingsSearch.Find(entries, "microphone").First().Key == "mic", "Exact label ranks first");
Check(PrototypeSettingsSearch.Find(entries, "  MICROPHONE  ").Count == 2, "Case and whitespace normalization");
Check(PrototypeSettingsSearch.Find(entries, "quiet speech").Single().Key == "quiet", "Description terms");
Check(PrototypeSettingsSearch.Find(entries, "audio headset").Single().Key == "mic", "Terms across category and choices");
Check(PrototypeSettingsSearch.Find(entries, "minimal").Single().Key == "overlay", "Overlay choice keywords");
Check(PrototypeSettingsSearch.Find(entries, "microphone impossible").Count == 0, "All terms required");
Check(PrototypeSettingsSearch.Find(entries, "zzznomatch").Count == 0, "No results");
Check(PrototypeSettingsSearch.Find(entries, " \t\n ").Count == 0, "Blank query");
