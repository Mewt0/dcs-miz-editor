# Local MizEdit translator

Create the personal Ollama profile:

```powershell
ollama pull qwen3:4b-instruct-2507-q4_K_M
ollama create mizedit-translator -f .\ollama\Modelfile
```

The profile uses the dedicated non-thinking Qwen3 4B Instruct model. The hybrid
`qwen3:4b` model is not used because it may spend time reasoning even when
`think: false` or `/no_think` is supplied.

Quick CLI check:

```powershell
ollama run mizedit-translator "Proceed to waypoint 3."
```

API clients may still send `"think": false`; it is harmless and documents the
intended behavior.

The public MizEdit application must remain usable without Ollama and must never download a model automatically.
