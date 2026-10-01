# AutoCAD AI Translator

An AutoCAD plugin that translates Chinese text inside drawings into English using any
OpenAI-compatible API (OpenAI, DeepSeek, Azure OpenAI, local gateways, ...).

Targets **.NET 8 (x64)** and works with AutoCAD 2024 / 2025 / 2026 and later x64 releases.

---

## Features

- **Ribbon tab** - an "AI 翻译" tab with buttons for every function, no command typing needed
- **Model settings dialog** - configure the API key, model, base URL, etc. in a window
  (Ribbon > 模型设置, or run `AI_TRANSLATE_SETTINGS`), with a built-in connection test
- **Choose the target language** - every run opens a preview dialog where you pick the
  language (英语 / 韩语 / 日语 / 俄语 / 德语 / 法语 …), so the plugin is no longer
  hardwired to English
- **Bilingual review before anything is written** -原文 and 译文 side by side, with one row
  per text entity. Edit any translation inline, untick the rows you do not want, then
  write only what you confirmed
- **Per-row confirmation** - rows are pre-ticked, so a typical run is review-and-apply
  rather than ticking eighty checkboxes
- **Persistent translation memory** - accepted translations are remembered in
  `%LOCALAPPDATA%\AutoCAD.AITranslate\translation-cache.json` and reused across drawings
  and sessions. Hand-edited rows are marked *verified* and are never overwritten by a
  later automatic run
- **Two write modes** - keep the original and drop the translation on a dedicated
  `AI_TRANSLATED` layer (双语对照), or overwrite in place (直接替换)
- **One command to translate a whole drawing** - scans ModelSpace and PaperSpace
- **Selection-scoped translation** - only processes what you pick
- **Multi-level rollback** - undo the last translation, then the one before it, etc.
- **CSV export** - dump the original/translated pairs for review
- **Batched, parallel requests** - dozens of strings per HTTP call, several calls at once
- **Response caching** - repeated labels cost no extra API calls
- Supports both the Responses and Chat Completions APIs with automatic fallback

---

## How a translation run works

```
翻译全图 / 翻译选区
       │
       ├─ 1. read-only scan ──────── collect Chinese text (no lock held)
       │
       ├─ 2. translation memory ──── serve anything already known (0 API calls)
       │
       ├─ 3. model call ──────────── only for text not in the memory
       │
       ├─ 4. 翻译预览 dialog
       │       ├── pick 目标语言
       │       ├── review 原文 | 译文, edit any row
       │       ├── untick rows to skip
       │       ├── 重译未确认项 (optional)
       │       └── pick 双语对照 or 直接替换
       │
       ├─ 5. remember ────────────── accepted rows → memory
       │                             edited rows   → memory, marked verified
       │
       └─ 6. write transaction ───── only confirmed rows, one short atomic step
```

Editing a translation in step 4 is the part worth knowing about: because the edited row is
stored as *verified*, the correction is reused for that term everywhere, in every later
drawing, and no automatic run can overwrite it.

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

The easiest way is the built-in settings dialog: **Ribbon "AI 翻译" tab > 模型设置**
(or run `AI_TRANSLATE_SETTINGS` in the command line). It edits
`AutoCAD.AITranslate.settings.json` next to the assembly, offers provider presets
(OpenAI / DeepSeek / 智谱 / 通义 / Moonshot), and has a "测试连接" button.

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
| `OPENAI_SYSTEM_PROMPT` | no | - | Extra style guidance, appended to the language instruction |
| `OPENAI_TIMEOUT_MS` | no | `60000` | Per-request timeout in milliseconds |
| `OPENAI_ORG` | no | - | Organization header |
| `OPENAI_PROJECT` | no | - | Project header |
| `OPENAI_TARGET_LANGUAGE` | no | `en` | Initial language in the preview dialog. Written back automatically after each run |

> **Never commit `AutoCAD.AITranslate.settings.json`.** It contains your key in plain text.
> It is already listed in `.gitignore`; commit the `.example` file instead.

`OPENAI_SYSTEM_PROMPT` no longer needs to name a language. Whatever it says about the
target language is overridden by the dialog, so a prompt written for English cannot fight
a request for Korean.

### Translation memory

| | |
|---|---|
| Location | `%LOCALAPPDATA%\AutoCAD.AITranslate\translation-cache.json` |
| Key | `(target language, source text)` |
| Saved | after every applied run, and on demand via `AI_TRANSLATE_SAVE_CACHE` |
| Cleared | `AI_TRANSLATE_CLEAR_CACHE` (removes the file as well as the in-memory copy) |

An entry marked `"verified": true` was edited by hand. Verified entries always win over
machine output and are never overwritten by a later automatic run. Delete a single bad
entry by editing the JSON, or clear everything with `AI_TRANSLATE_CLEAR_CACHE`.

### DeepSeek example

```json
{
  "OPENAI_API_KEY": "YOUR_KEY",
  "OPENAI_MODEL": "deepseek-chat",
  "OPENAI_BASE_URL": "https://api.deepseek.com",
  "OPENAI_API_TYPE": "chat_completions",
  "OPENAI_SYSTEM_PROMPT": "专业工程图纸用语，简洁准确。",
  "OPENAI_TARGET_LANGUAGE": "en",
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
| `AI_TRANSLATE_SETTINGS` | Open the model settings dialog |
| `AI_TRANSLATE_SAVE_CACHE` | Write the translation memory to disk immediately |
| `AI_TRANSLATE_CLEAR_CACHE` | Free the in-memory cache *and* the translation memory |

All commands are also available as buttons on the **"AI 翻译"** Ribbon tab: 翻译全图 /
翻译选区 / 回滚翻译 / 模型设置 / 保存译文库 / 清空缓存. The tab is created when the plugin loads.

> The command names still say `ZH2EN` for backwards compatibility, but the target
> language is now chosen per run in the preview dialog. `OPENAI_TARGET_LANGUAGE` in the
> settings file supplies the initial selection.

### Typical run

```
Command: AI_TRANSLATE_ZH2EN
Found 412 item(s) containing Chinese text.
  Translating... 10% (3/21 batches)
  ...
  Translation stage finished in 38.4s.

          ┌─────────────────────────────────────────────────────────┐
          │  翻译预览 — 共 412 处文本                                │
          │  目标语言 [英语 (English) v]  写入方式 (o)双语对照 ( )替换│
          │  ┌──┬──────────────┬──────────────────────┬───────────┐ │
          │  │ ˅│ 一层平面图     │ Ground Floor Plan    │ 缓存      │ │
          │  │ ˅│ 设备材料表     │ Equipment BOM        │ 机器翻译  │ │
          │  │  │ 给水排水       │ Water supply & drain │ 手动修改  │ │
          │  └──┴──────────────┴──────────────────────┴───────────┘ │
          │  已选 410 / 412 行写入   手动修改 1 行；缓存命中 1 行    │
          │           [全部写入][全部跳过][重译未确认项] [写入图纸]  │
          └─────────────────────────────────────────────────────────┘

Translation completed: 410 item(s) applied to 英语 (English).
  Edited by hand: 1 | Translation memory: 187 entries (1 verified)
  Use the Ribbon "回滚翻译" button (or AI_TRANSLATE_ROLLBACK) to undo.
CSV exported: C:\Users\me\Desktop\translations-20260926-191500.csv
```

The console no longer asks any questions: the language, the write mode and the per-row
decisions all come from the dialog.

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
    ├── RibbonSetup.cs                     Ribbon tab and buttons
    ├── SettingsDialog.xaml(.cs)           Model settings dialog (WPF)
    ├── TranslationReviewDialog.xaml(.cs)  Language picker + bilingual review (WPF)
    ├── TranslationRow.cs                  One reviewable row: confirm / edit / status
    ├── TargetLanguages.cs                 Language catalogue and prompt composition
    ├── TranslationCache.cs                Persistent translation memory (JSON)
    ├── OpenAiClient.cs                    HTTP transport, API protocol, JSON parsing
    ├── TranslationService.cs              Batching, caching, configuration
    ├── EndpointProbeStore.cs              Remembers which API route a gateway serves
    ├── ModelPresets.cs                    Saved provider presets
    ├── TranslationSession.cs              Per-document rollback state
    ├── TranslationModels.cs               Data models
    ├── Diagnostics.cs                     Append-only log in %TEMP%
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
