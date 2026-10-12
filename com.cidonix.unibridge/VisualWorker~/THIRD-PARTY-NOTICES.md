# Third-party notices

UniBridge's worker and byte-level BPE orchestration are independently authored.
No source code or assets from Unity Assistant are incorporated.

## ONNX Runtime 1.23.2

Microsoft.ML.OnnxRuntime, Copyright Microsoft Corporation, MIT License.
Official runtime/package: https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.23.2
Full license: `LICENSE-onnxruntime.txt`; upstream transitive notices:
`ThirdPartyNotices-onnxruntime.txt`.

## CLIP model and tokenizer data

OpenAI CLIP, Copyright (c) 2021 OpenAI, MIT License.
Official original: https://github.com/openai/CLIP
Full license: `LICENSE-CLIP.txt`.

The external ONNX model artifacts are the Hugging Face provider's conversion:
https://huggingface.co/Xenova/clip-vit-base-patch32
Immutable revision: `6ef1ebc8b0766a7a8d11b146462c99cdf74dd22d`.
Pinned sizes and SHA-256 are in `Program.cs` and `model-manifest.json`.
Model weights/vocabulary are downloaded only by explicit installation and
retained outside authored Unity assets. Query language for this provider is
English; model similarity is a retrieval ranking signal, not a probability.

## .NET self-contained runtime

The worker is published using the .NET 10 SDK and contains the .NET runtime.
Microsoft .NET runtime is MIT; the full runtime license and third-party notices
are retained in `LICENSE-dotnet.txt` and `ThirdPartyNotices-dotnet.txt`.
