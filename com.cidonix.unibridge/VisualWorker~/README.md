# UniBridge visual embedding worker

Independent, optional Editor companion. Runs genuine local CLIP text/image
embedding inference with ONNX Runtime CPU. It is separate from Unity's Mono
process and adds no Unity Player assembly or Sentis dependency.

The supported model is `clip-vit-b32-int8-v1`, English text, 512-dimensional
unit-normalized vectors. Cosine similarity ranks visual content; filenames,
asset names and labels are not embedding inputs. Similarity is not probability.

Run the self-contained executable with exactly `--stdio`; send one JSON line.
The process accepts one request and emits one JSON response, then exits.
No command shell is used. The request maximum is 65,536 bytes, query maximum
2,048 characters / 75 content tokens, image tensor exactly 602,112 bytes.
The default operation deadline is 15 seconds; the parent owns cancellation
and may terminate only its own worker process. ONNX inference additionally
uses `RunOptions.Terminate` when its operation deadline expires.

Actions: `status`, `install`, `tokenize`, `encode_text`, `encode_image`.
Every request needs `requestId`; every response echoes it. Text requests need
`text`. Image requests need the task-generated `tensorFile` within `jobRoot`.
`modelDirectory` is an optional external model cache override; it is never an
authored Unity object reference or a tracked machine-specific path.

Prepare image input as float32 little-endian CHW `[1,3,224,224]`, RGB, top row
first. Resize the shortest edge to 224, center crop 224 square, composite
transparent pixels over white, and normalize `(channel / 255 - mean) / std`.
Mean: `[0.48145466,0.4578275,0.40821073]`.
Std: `[0.26862954,0.26130258,0.27577711]`.

Installation is explicitly opt-in: `action=install,allowDownload=true`.
The installer requests only pinned model artifact URLs, checks length and
SHA-256, refuses to overwrite unexpected existing files, and atomically
publishes each verified download. Status, tokenization and inference never
download or contact a service. Default model cache derives from the current
user profile and contains the provider ID and pinned revision.

Provider: Hugging Face `Xenova/clip-vit-base-patch32`, pinned revision
`6ef1ebc8b0766a7a8d11b146462c99cdf74dd22d`, an independent ONNX conversion
of the OpenAI CLIP ViT-B/32 model. The provider's public model data are reused;
no Unity Assistant code, assets or dependencies are copied.

Official sources:

- https://huggingface.co/Xenova/clip-vit-base-patch32
- https://github.com/openai/CLIP
- https://github.com/openai/CLIP/blob/main/LICENSE
- https://onnxruntime.ai/docs/get-started/with-csharp.html
- https://github.com/microsoft/onnxruntime/blob/v1.23.2/LICENSE

Runtime: `Microsoft.ML.OnnxRuntime` 1.23.2 (MIT). Model/code provenance and
redistribution notices are retained in `THIRD-PARTY-NOTICES.md` and the adjacent
full upstream license files. Model weights are downloaded into the external
cache and are not embedded in the worker or Unity package.

Build using .NET 10:

```
dotnet publish UniBridge.VisualWorker.csproj -c Release -r win-x64 --self-contained true
```

The companion supports the four release RIDs `win-x64`, `linux-x64`, `osx-x64`,
`osx-arm64`. The Windows x64 CPU backend has native qualification; the other
published binaries require physical-platform qualification before claiming
equivalent native behavior. Missing executable/model reports inactive/error,
never a heuristic fallback masquerading as semantic search.
