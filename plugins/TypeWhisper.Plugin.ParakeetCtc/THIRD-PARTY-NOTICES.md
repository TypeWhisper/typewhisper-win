# Model and algorithm references

The BPE merge algorithm, per-token constrained CTC score normalization and adaptive context-bias policy follow FluidInference/FluidAudio 0.15.5, revision `19600a485baa4998812e4654b70d2bab8f2c9949` (Apache-2.0). These are C# adaptations; the full FluidAudio pipeline is not bundled. Sources: `BpeTokenizer.swift`, `CtcDPAlgorithm.swift`, `VocabularyRescorer.swift`, `VocabularyRescorer+TokenEvaluation.swift`, `ContextBiasingConstants.swift`. The separate tokenizer asset is from the official FluidInference model repository; its checksum and validation requirements are documented in README.md.

The model is the NVIDIA NeMo Parakeet TDT/CTC 110M English CTC-head export distributed by k2-fsa/sherpa-onnx. It is not included in this repository.

- Archive: `sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2`, GitHub `k2-fsa/sherpa-onnx`, release `asr-models`.
- Archive SHA-256: `17f945007b52ccd8b7200ffc7c5652e9e8e961dfdf479cefcabd06cf5703630b`.
- Feature configuration: sherpa-onnx `offline-recognizer-ctc-impl.h`, `offline-stream.cc` and kaldi-native-fbank `feature-window.cc` (Apache-2.0 projects). The managed feature implementation is independently written from the documented parameters: periodic Hann, reflected Kaldi frame placement, preemphasis, Slaney mel filters and per-feature normalization.

This Windows CTC adapter does not claim numerical or behavioral equivalence to FluidAudio's CoreML rescorer. The model is English; multilingual vocabulary behavior requires separate validation.
