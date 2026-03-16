# AutoCAD 2025 AI Translator (.NET 8)

AutoCAD 2025 plugin to translate Chinese text in drawings to English using an OpenAI-compatible API.

## Features

- One command to translate all Chinese text to English
- Selection-only translation command
- Optional output to a new layer (preserve original text)
- Preview before applying
- Rollback last translation
- Export translation pairs to CSV
- Supports `DBText` and `MText`
- Preserves numbers, units, punctuation, and line breaks
- Supports both Responses and Chat Completions APIs (auto fallback)

## Prerequisites

- AutoCAD 2025 (x64)
- .NET 8 SDK (x64)
- Visual Studio 2022 (optional, for IDE builds)

## Build

PowerShell build (recommended):

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`
- Optional: `-AutoCADPath "C:\Program Files\Autodesk\AutoCAD 2025"`
- Optional: `-OnlineRestore` to use online NuGet sources

Build output:

- `src/AutoCAD.AITranslate/bin/Release/net8.0-windows/AutoCAD.AITranslate.dll`

## Configure API

Environment variables take priority. You can also place a local config file next to the DLL:

- `AutoCAD.AITranslate.settings.json`

Supported settings:

- `OPENAI_API_KEY` (required)
- `OPENAI_MODEL` (optional, default `gpt-4.1`)
- `OPENAI_BASE_URL` (optional, default `https://api.openai.com/v1`)
- `OPENAI_API_TYPE` (optional, `responses`, `chat_completions`, or `auto`, default `auto`)
- `OPENAI_SYSTEM_PROMPT` (optional, system message for chat-based APIs)
- `OPENAI_TIMEOUT_MS` (optional, request timeout in milliseconds, default `30000`)
- `OPENAI_ORG` (optional)
- `OPENAI_PROJECT` (optional)

### DeepSeek example

```json
{
  "OPENAI_API_KEY": "YOUR_KEY",
  "OPENAI_MODEL": "deepseek-chat",
  "OPENAI_BASE_URL": "https://api.deepseek.com",
  "OPENAI_API_TYPE": "chat_completions",
  "OPENAI_SYSTEM_PROMPT": "You are a helpful assistant.",
  "OPENAI_TIMEOUT_MS": "30000"
}
```

Note: DeepSeek uses `/chat/completions`, so do not add `/v1` to the base URL.

## Install / Load

1. Build the DLL
2. In AutoCAD, run `NETLOAD`
3. Load `AutoCAD.AITranslate.dll`

## Use

- `AI_TRANSLATE_ZH2EN`
- `AI_TRANSLATE_ZH2EN_SEL` (translate selected text only)
- `AI_TRANSLATE_ROLLBACK` (undo last translation in the current session)

## Notes

- The command scans ModelSpace and PaperSpace and translates any text containing Chinese characters.
- Use the `NewLayer` option to write translated text onto the `AI_TRANSLATED` layer while keeping the original unchanged.
- CSV export is optional and will prompt for a save path after translation.

## Troubleshooting

- 404 errors with DeepSeek usually mean the wrong endpoint. Set `OPENAI_API_TYPE` to `chat_completions` and use `https://api.deepseek.com` as the base URL.
- If the command appears to hang, lower `OPENAI_TIMEOUT_MS` to fail fast and inspect the error message.
