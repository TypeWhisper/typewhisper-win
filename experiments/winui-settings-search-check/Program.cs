using TypeWhisper.WinUI;

SettingSearchEntry[] entries =
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
Check(SettingsSearchIndex.Find(entries, "microphone").First().Key == "mic", "Exact label ranks first");
Check(SettingsSearchIndex.Find(entries, "  MICROPHONE  ").Count == 2, "Case and whitespace normalization");
Check(SettingsSearchIndex.Find(entries, "quiet speech").Single().Key == "quiet", "Description terms");
Check(SettingsSearchIndex.Find(entries, "audio headset").Single().Key == "mic", "Terms across category and choices");
Check(SettingsSearchIndex.Find(entries, "minimal").Single().Key == "overlay", "Overlay choice keywords");
Check(SettingsSearchIndex.Find(entries, "microphone impossible").Count == 0, "All terms required");
Check(SettingsSearchIndex.Find(entries, "zzznomatch").Count == 0, "No results");
Check(SettingsSearchIndex.Find(entries, " \t\n ").Count == 0, "Blank query");
