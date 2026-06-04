# DCS Miz Editor

<p align="center">
  <img src="assets/apache-showcase.jpg" alt="Apache helicopter showcase">
</p>

<p align="center">
  A Windows editor for DCS World <code>.miz</code> missions: briefings, localization, media resources, kneeboard pages, radio subtitles, scripts and mission triggers.
</p>

<p align="center">
  <strong>Built for mission maintenance.</strong> Open a mission archive, edit the Lua mission table and localized resources, then write a valid <code>.miz</code> back to disk.
</p>

## Showcase

The screenshots below show a local showcase mission renamed to `Operation Apache Forge` for presentation. Mission payload files are not included in this repository.

### Briefing And Localization

![Briefing editor](docs/assets/screenshots/01-briefing.png)

MizEdit resolves `DictKey_*` briefing text through `l10n/<locale>/dictionary`, supports multiple locales such as `DEFAULT` and `RU`, and writes changed briefing fields back before saving.

### Mission Pictures

![Picture resource editor](docs/assets/screenshots/02-pictures.png)

Briefing images are read through `pictureFileNameB` and `mapResource` keys. You can add, replace or remove image resources without manually editing the archive.

### Audio Resources

![Audio resource editor](docs/assets/screenshots/03-audio.png)

Audio files are listed from `l10n/<locale>` and `mapResource`. Replacing a resource keeps the same `ResKey_*`, so existing trigger and route/task references keep working.

### Trigger And Route Task Scan

![Trigger and task action scan](docs/assets/screenshots/04-triggers.png)

Many DCS missions do not store most logic as simple classic triggers. MizEdit recursively scans route/task tables and shows actions such as `ComboTask`, `WrappedAction`, `TransmitMessage`, `SetFrequency`, `Script`, `EngageTargets` and other nested mission logic.

### Radio Subtitle Editing

![Transmit radio subtitle editor](docs/assets/screenshots/05-radio.png)

`TransmitMessage` actions are extracted with their group/task context, subtitle dictionary key, audio `ResKey_*`, duration and editable subtitle text. Saving a subtitle updates the active locale dictionary while the mission action keeps pointing at the same keys.

## What It Can Edit

- Mission name, sortie, description, red task and blue task.
- Localized dictionary values for `DEFAULT`, `RU` and other `l10n` locales.
- `mapResource` entries with generated `ResKey_*` keys.
- Briefing pictures, trigger pictures and kneeboard images.
- Audio resources under `l10n/<locale>`.
- Existing audio/image replacement while preserving the same `ResKey_*`.
- Lua script resources under `l10n/<locale>`.
- Simple Mission Start triggers for playing added audio or running added scripts.
- Radio subtitles from `TransmitMessage` route/task actions.
- Batch TXT export/import for briefing localization work.

## DCS Mission Structure

A `.miz` file is a zip archive. Important entries:

- `mission` - Lua table containing most mission logic.
- `options`, `warehouses`, `theatre` - mission metadata.
- `l10n/<locale>/dictionary` - localized text by `DictKey_*`.
- `l10n/<locale>/mapResource` - resource file mapping by `ResKey_*`.
- `l10n/<locale>/*` - audio, images, scripts and other resources.
- `KNEEBOARD/IMAGES/*` - kneeboard pages.

MizEdit extracts the archive to a temporary work directory, edits these files, then repacks the mission as a `.miz`.

## Save Safety

`Save` and `Save as .miz` apply pending UI changes before writing:

- briefing fields are written to `mission` or `dictionary`;
- selected radio subtitle changes are written to the active locale dictionary;
- the currently opened Lua script is written to the mission work directory;
- added/replaced resources are already present in the work directory and `mapResource`.

This means you can edit fields and press `Save` directly. `Apply` is still available for explicit briefing updates, but it is not required before saving.

## Resource Safety

Adding a new file creates a `ResKey_*` entry in `mapResource` and copies the file into `l10n/<locale>`.

Replacing an existing resource keeps the same key and changes only the file behind it:

```lua
["file"] = "ResKey_advancedFile_31"
```

Keeping the same key is important because existing `TransmitMessage`, trigger and route/task actions continue to point at the correct resource after the file is replaced.

## Build

Requirements:

- Windows
- .NET SDK 10

Build Debug:

```powershell
dotnet build "mizedit c#.sln"
```

Build Release:

```powershell
dotnet build "mizedit c#.sln" -c Release
```

Run:

```powershell
dotnet run --project "mizedit.csproj"
```

Open a mission directly:

```powershell
dotnet run --project "mizedit.csproj" -- "C:\path\to\mission.miz"
```

## Important Git Note

Do not commit DCS mission payloads. `.miz`, `.zip` and `.pdf` files are ignored intentionally because campaign missions can be large and may contain licensed campaign content.

The repository contains source code, docs, icons and screenshots only. Local test missions belong in `work/`, which is ignored.

## Current Limits

- Full visual editing of every possible DCS route/task action is not implemented yet.
- Simple classic trigger creation is supported, but a full Mission Editor-style condition/action builder is future work.
- Some complex campaign logic is best inspected first, then edited carefully in focused fields such as resource keys, subtitle dictionaries and simple trigger actions.
