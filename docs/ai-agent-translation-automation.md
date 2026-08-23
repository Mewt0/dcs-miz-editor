# MizEdit AI Agent Translation Automation

## Goal

Add an automated translation workflow for DCS mission text without sending bulky hidden markers to the model and without applying unverified output directly to `.miz` files.

## Proposed User Flow

1. User opens a mission or a folder of missions.
2. User clicks `AI agent translation`.
3. MizEdit creates a run folder with:
   - `manifest.json`
   - small `chunk-001.jsonl`, `chunk-002.jsonl`, etc.
   - one task file per agent with translation rules.
4. Agents fill `translated/chunk-001.jsonl`, preserving `Number`, `Key`, and `Source`.
5. MizEdit validates every returned chunk:
   - JSONL parses.
   - row count matches.
   - `Number`, `Key`, and `Source` match exactly.
   - `Translation` is not empty.
   - protected tokens, placeholders, frequencies, headings, and Lua-like content are unchanged.
6. MizEdit shows preview:
   - translated rows;
   - unchanged rows;
   - missing rows;
   - rejected rows;
   - suspicious length/untranslated warnings.
7. User clicks `Apply` only after preview.
8. MizEdit backs up original `.miz`, writes RU locale, validates reopen, and reports replaced archive files.

## Chunk Format

Current safe internal format:

```jsonl
{"Number":1,"Key":"DictKey_ActionText_105","Source":"Original text","Translation":""}
```

For external workers, a more token-efficient variant can be used:

```jsonl
{"n":1,"k":"DictKey_ActionText_105","t":"Russian translation"}
```

MizEdit should then merge this compact response back onto the original manifest by `n + k`, so agents do not need to echo the full source text. This saves many tokens on long tutorial missions.

## Recommended Defaults

- Chunk size: 20 to 30 rows for normal LLM workers.
- Chunk size: 60 to 80 rows only for strong agents with file access and long outputs.
- Deduplicate exact repeated source lines before export.
- Keep technical terms stable: `WSO`, `JESTER`, `INS`, `TACAN`, `UHF`, `VHF`, `AWACS`, `kneeboard`, `throttle`, switch labels, coordinates, headings, and frequencies.
- Never use Ollama for this workflow unless the user explicitly enables a local private provider.

## UI Button Shape

Add a split button near translation tools:

- `Export for AI agents`
- `Import agent results`
- `Translate selected mission with agents`
- `Translate folder with agents`

The fully automated options should still stop at preview before modifying any `.miz`.

## Hidden Bugs To Watch

- Worker changes `Source` text while translating.
- Worker drops one JSONL line and shifts all following translations.
- Worker translates switch names that must match cockpit labels.
- Worker changes `\n`, `%s`, `{0}`, frequencies, headings, coordinates, or file names.
- Worker returns Markdown fences around JSONL.
- A mission changes after export, making the manifest stale.
- Duplicate source text gets translated inconsistently across missions.

## Current Implementation Notes

- User-facing copy now always exports the compact `Batch` plus `1»` line format.
- M2/MZ markers remain supported only for import compatibility.
- `tests/F4E.Translation --agent-export` supports configurable chunk size from 5 to 80 rows.
- Import should remain strict and reject any chunk that fails exact structural validation.
