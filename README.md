# AutoCAD AI Translator

An AutoCAD plugin that translates Chinese text inside drawings into English using any
OpenAI-compatible API (OpenAI, DeepSeek, Azure OpenAI, local gateways, ...).

Targets **.NET 8 (x64)** and works with AutoCAD 2024 / 2025 / 2026 and later x64 releases.

---

## Features

- **One command to translate a whole drawing** - scans ModelSpace and PaperSpace
- **Selection-scoped translation** - only processes what you pick
- **Non-destructive by default** - translated copies go on a dedicated `AI_TRANSLATED`
  layer and the originals are left untouched
- **Preview before applying** - see the first few results, then confirm
- **Multi-level rollback** - undo the last translation, then the one before it, etc.
- **CSV export** - dump the original/translated pairs for review
- **Batched, parallel requests** - dozens of strings per HTTP call, several calls at once
- **Response caching** - repeated labels cost no extra API calls
- **Dry-run mode** - translate and inspect without touching the drawing
- Supports both the Responses and Chat Completions APIs with automatic fallback

---

## Requirements

| Component | Notes |
|---|---|
| AutoCAD 2024 / 2025 / 2026 (x64) | Managed API assemblies are referenced from the install directory |
| **.NET 8 SDK (x64)** | Required to *build*. The .NET runtime alone is not enough |
| Visual Studio 2022 | Optional. The `build.ps1` script works with the standalone Build Tools |
| An OpenAI-compatible API key | OpenAI, DeepSeek, Azure, ... |

---

## Build

```powershell
# Auto-detects the newest installed AutoCAD under Program Files\Autodesk
.\build.ps1

# Or point it at a specific installation
.\build.ps1 -AutoCADPath "C:\Program Files\Autodesk\AutoCAD 2026"

# Use online NuGet sources instead of the offline package cache
.\build.ps1 -OnlineRestore
```

The script reports missing prerequisites (SDK, MSBuild, AutoCAD assemblies) up front
instead of failing with an opaque compiler error.

Output:

```
src\AutoCAD.AITranslate\bin\Release\net8.0-windows\AutoCAD.AITranslate.dll
```

---

## Configuration

Environment variables take precedence over the JSON settings file. The file is read from
the directory containing the assembly:

```
AutoCAD.AITranslate.settings.json
```

Start from the committed template:

```powershell
Copy-Item .\src\AutoCAD.AITranslate\AutoCAD.AITranslate.settings.json.example `
          .\src\AutoCAD.AITranslate\bin\Release\net8.0-windows\AutoCAD.AITranslate.settings.json
```

| Setting | Required | Default | Description |
|---|---|---|---|
| `OPENAI_API_KEY` | yes | - | API key |
| `OPENAI_MODEL` | no | `gpt-4.1` | Model name |
| `OPENAI_BASE_URL` | no | `https://api.openai.com/v1` | Base URL, no trailing slash |
| `OPENAI_API_TYPE` | no | `auto` | `responses`, `chat_completions`, or `auto` |
| `OPENAI_SYSTEM_PROMPT` | no | - | System message, chat-based APIs only |
| `OPENAI_TIMEOUT_MS` | no | `60000` | Per-request timeout in milliseconds |
| `OPENAI_ORG` | no | - | Organization header |
| `OPENAI_PROJECT` | no | - | Project header |

> **Never commit `AutoCAD.AITranslate.settings.json`.** It contains your key in plain text.
> It is already listed in `.gitignore`; commit the `.example` file instead.

### DeepSeek example

```json
{
  "OPENAI_API_KEY": "YOUR_KEY",
  "OPENAI_MODEL": "deepseek-chat",
  "OPENAI_BASE_URL": "https://api.deepseek.com",
  "OPENAI_API_TYPE": "chat_completions",
  "OPENAI_SYSTEM_PROMPT": "You are a helpful assistant.",
  "OPENAI_TIMEOUT_MS": 60000
}
```

DeepSeek exposes `/chat/completions` at the root, so **do not** append `/v1` to the base URL.

---

## Install / Load

1. Build the DLL (see above)
2. In AutoCAD, run `NETLOAD`
3. Select `AutoCAD.AITranslate.dll`

The plugin prints a short confirmation with the detected AutoCAD version when it loads.

---

## Commands

| Command | Purpose |
|---|---|
| `AI_TRANSLATE_ZH2EN` | Translate all Chinese text in ModelSpace and PaperSpace |
| `AI_TRANSLATE_ZH2EN_SEL` | Translate only the selected text |
| `AI_TRANSLATE_ROLLBACK` | Undo the most recent translation in the current drawing |
| `AI_TRANSLATE_CLEAR_CACHE` | Free the in-memory translation cache |

### Typical run

```
Command: AI_TRANSLATE_ZH2EN
Translation mode [Replace in place / New layer keeps originals] <NewLayer>: <Enter>
Items per API request (1 = one call per item) <20>: <Enter>
Concurrent requests <4>: <Enter>
Write changes to the drawing? [Yes/PreviewOnly] <Yes>: <Enter>

Found 412 item(s) containing Chinese text.
Model: deepseek-chat | Batch: 20 | Parallel: 4
  Translating... 10% (3/21 batches)
  ...
  Translation stage finished in 38.4s.
412 item(s) ready to translate. Sample:
  [1] 一层平面图
   -> Ground Floor Plan
  ...
Apply translation? [Yes/No] <No>: Yes

Translation completed: 412 item(s) applied.
  Unique strings: 187 | API requests: 10 | Cache hits: 0
  Run AI_TRANSLATE_ROLLBACK to undo, or AI_TRANSLATE_CLEAR_CACHE to free memory.
CSV exported: C:\Users\me\Desktop\translations-20260926-191500.csv
```

### Modes

- **NewLayer** (default) - appends translated copies on the `AI_TRANSLATED` layer.
  Your original text is preserved, so this mode is safe and fully reversible.
- **Replace** - overwrites the original text in place. Smaller drawing, but the original
  wording is gone unless you roll back.

> Text inside **block definitions** is always replaced in place, even in NewLayer mode.
> A block definition is shared by every one of its references, so cloning into it would
> rewrite the whole drawing. Explode such blocks first if you want the original text kept.

---

## Performance notes

The batch size and concurrency prompts exist because translation cost scales with the
number of HTTP round trips, not the number of characters:

| Drawing size | Batch 1, parallel 1 | Batch 20, parallel 4 |
|---|---|---|
| 100 labels | ~2-5 min | ~10-20 s |
| 500 labels | ~12-25 min | ~30-60 s |
| 1000 labels | ~25-50 min | ~1-2 min |

Guidance:

- **Keep the defaults** (batch 20, parallel 4) unless you hit rate limits
- **Lower concurrency** (2 or even 1) if the API returns 429 responses
- **Raise the batch size** for very large drawings, but note that a bigger batch means a
  bigger prompt and a higher chance of a malformed reply; the client falls back to
  per-item requests automatically when that happens
- Repeated identical labels are translated **once** and then served from the cache
- Translation happens **outside** the AutoCAD transaction, so the drawing lock is only
  held during the brief write step at the end
- Use **PreviewOnly** to estimate cost and quality before committing to a full run
- The cache is per AutoCAD session; `AI_TRANSLATE_CLEAR_CACHE` resets it

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `OpenAI API key not configured` | Set `OPENAI_API_KEY`, or create `AutoCAD.AITranslate.settings.json` next to the DLL |
| `Endpoint not found (404)` | For DeepSeek and similar gateways set `OPENAI_API_TYPE` to `chat_completions` and remove `/v1` from `OPENAI_BASE_URL` |
| `timed out after 60000 ms` | Raise `OPENAI_TIMEOUT_MS`, or lower the batch size |
| `API error 429` | Rate limited. Lower the concurrency prompt to 2 or 1 |
| Errors mention `non-JSON body` | The gateway returned an HTML error page. The message now includes the raw body; check the URL and your proxy |
| Translation stopped partway | Individual failures are reported at the end and do not abort the run. Re-run with `PreviewOnly` off to retry the remainder |
| Some block text was skipped | Nested text belongs to a shared block definition. Explode the block and re-run |
| Build fails: `AcMgd.dll not found` | Pass `-AutoCADPath` to `build.ps1` |
| Build fails: SDK not found | Install the **.NET 8 SDK**, not just the runtime |

---

## Repository layout

```
AUTOCAD2025_AI_TRANSLATE_CH_EN/
├── build.ps1
├── LICENSE
├── README.md
└── src/AutoCAD.AITranslate/
    ├── Commands.cs                        AutoCAD command entry points and UI
    ├── OpenAiClient.cs                    HTTP transport, API protocol, JSON parsing
    ├── TranslationService.cs              Batching, caching, configuration
    ├── TranslationSession.cs              Per-document rollback state
    ├── TranslationModels.cs               Data models
    ├── CsvExporter.cs                     CSV export
    ├── LayerUtils.cs                      Layer and block-definition helpers
    ├── RegexUtils.cs                      Chinese detection
    ├── AutoCAD.AITranslate.csproj
    └── AutoCAD.AITranslate.settings.json.example
```

---

## License

MIT. See [LICENSE](LICENSE).

This project is an independent tool and is not affiliated with or endorsed by Autodesk.
AutoCAD is a trademark of Autodesk, Inc. Ensure your use complies with the licensing
terms of AutoCAD and of your chosen translation API provider.
