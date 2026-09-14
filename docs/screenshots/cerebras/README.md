# Cerebras development acceptance

Captured on September 14, 2026 from the native WinUI development host built from this checkout and the installed Cerebras `1.1.1` package. Published and checkout host DLL hashes matched (`37BE0715D34593845D2387BB80CAFD596F438D27E5B9E314AB01EBC2CD3730EF`).

- [Saved settings](settings-dark.jpg): native sidebar branding, saved-key state, GPT-OSS 120B selection, provider-default temperature and the disabled Save settings button after restart.
- [Custom temperature](custom-temperature-dark.jpg): switching the draft to Custom reveals the temperature field and enables Save settings. The preceding provider-default setting was restored without saving the draft.

The real immutable-store update from `1.1.0` to `1.1.1` first reported a pending restart, then promoted successfully during the prescribed development-host restart. No update warning remained. Hashes of the Cerebras settings and encrypted secret files were unchanged, and every unrelated installation receipt was preserved.

Authenticated connection validation and model discovery succeeded. Actual text requests with GPT-OSS 120B and Qwen 3.8 27B returned HTTP 402 because the account required credit/quota. The operator declined billing changes and further inference tests. These images verify the settings UI; they do not prove successful inference, microphone capture or paste.
