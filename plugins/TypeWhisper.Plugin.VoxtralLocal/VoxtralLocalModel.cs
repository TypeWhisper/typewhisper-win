namespace TypeWhisper.Plugin.VoxtralLocal;

/// <summary>One GGUF file of Voxtral Mini 4B Realtime, pinned to the size and SHA-256 it has at the pinned revision.</summary>
internal sealed record VoxtralLocalModel(string Id, string DisplayName, string FileName, string DownloadUrl, long SizeBytes, string Sha256)
    : IDownloadableModel
{
    // The transcribe.cpp project's GGUF conversion of mistralai/Voxtral-Mini-4B-Realtime-2602. The URL names a commit
    // instead of main because native code parses the file and main can serve different bytes under the same address.
    // Pinned on 2026-10-27 to commit 7fd3d424 of 2026-10-16; the SHA-256 is the LFS object id from
    // https://huggingface.co/api/models/handy-computer/Voxtral-Mini-4B-Realtime-2602-gguf/tree/<commit> and matched a download.
    internal const string Revision = "7fd3d424fc558edca2c53808578b0541ef52a81a";
    internal const string BaseUrl = "https://huggingface.co/handy-computer/Voxtral-Mini-4B-Realtime-2602-gguf/resolve/" + Revision;

    // Q4_K_M matched Q8_0 on German dictation (9.6 % against 9.8 % word errors on 40 own recordings) and runs faster;
    // the publisher measured the quantization ladder as accuracy-neutral down to Q4_K_M.
    internal static readonly VoxtralLocalModel MiniQ4 = new("voxtral-mini-4b-realtime-q4", "Voxtral Mini 4B Realtime",
        "Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf", BaseUrl + "/Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf",
        2_830_493_984, "39dc1f65539373a406edea7490505822d77c12edff521744678717eef4da4723");
}
