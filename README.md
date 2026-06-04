# DCS Miz Editor

DCS Miz Editor, also called MizEdit in the application UI, is a Windows WPF editor for DCS World `.miz` missions. It opens a mission archive, edits the Lua mission table and localized resources, then writes the changes back into a valid `.miz` archive.

The project is aimed at campaign and mission maintenance work: briefing text, multilingual dictionaries, pictures, kneeboard pages, audio resources, Lua scripts, and route/task action inspection.

Recommended GitHub repository name:

```text
dcs-miz-editor
```

Search keywords: DCS World mission editor, DCS miz editor, .miz editor, DCS mapResource editor, DCS l10n dictionary editor, DCS campaign mission tools.

## What It Can Edit

- Mission briefing fields: mission name, sortie, description, red task, blue task.
- Localized `dictionary` values for `DEFAULT`, `RU`, and other `l10n` locales.
- `mapResource` entries with generated `ResKey_*` keys.
- Briefing pictures in `pictureFileNameB`.
- Trigger pictures in `triggerPictures`.
- Kneeboard images under `KNEEBOARD/IMAGES`.
- Audio files under `l10n/<locale>`.
- Existing audio/picture resource replacement while keeping the same `ResKey`.
- Lua scripts under `l10n/<locale>`.
- Simple Mission Start triggers for playing an added audio resource or running an added script.
- Radio subtitles for `TransmitMessage` route/task actions.

## DCS Mission Structure

A `.miz` file is a zip archive. Important entries:

- `mission` - Lua table containing most mission logic.
- `options`, `warehouses`, `theatre` - DCS mission metadata.
- `l10n/<locale>/dictionary` - localized text by `DictKey_*`.
- `l10n/<locale>/mapResource` - resource file mapping by `ResKey_*`.
- `l10n/<locale>/*` - audio, images, scripts and other resource files.
- `KNEEBOARD/IMAGES/*` - kneeboard pages.

Many campaign missions do not use classic `mission.trig` triggers. For example, MAD AH-64D and A-10C Outpost store most mission logic in route/task actions such as `ComboTask`, `WrappedAction`, `TransmitMessage`, `SetFrequency`, `EngageTargets`, and similar nested task tables. MizEdit therefore scans both classic triggers and route/task actions.

## Save Behavior

`Save` and `Save as Miz` apply pending UI changes before writing the archive:

- current briefing fields are written to `mission` or `dictionary`;
- selected radio subtitle changes are written to the active locale dictionary;
- the currently opened Lua script is written to the mission work directory;
- added/replaced resources are already present in the work directory and `mapResource`.

This means a user can edit fields and press `Save` directly. Pressing `Apply` is still available, but it is not required before saving.

## Resource Safety

Adding a new file creates a `ResKey_*` entry in `mapResource` and copies the file into `l10n/<locale>`.

Replacing an existing resource keeps the same key and changes only the file behind it. This is important for existing DCS actions:

```lua
["file"] = "ResKey_advancedFile_31"
```

If that key is kept, existing `TransmitMessage` or trigger actions continue to point at the same resource key after the audio/image file is replaced.

## Tested Scenarios

Functional tests were run on real installed DCS campaign missions, using copies only.

### MAD AH-64D `M04.miz`

- Locales: `DEFAULT`, `RU`
- `ext_loader.library`: `Mods/campaigns/MAD AH-64D/MADAH64D`
- `miz_id`: preserved
- `mapResource`: 109 default resources
- Radio `TransmitMessage`: 79 found after recursive route/task scan
- Route/task actions: 492
- Save/reload smoke test passed

### A-10C Outpost `M3OUTPOSTA10.miz`

- Locales: `DEFAULT`, `RU`
- Default `mapResource`: 276 resources
- RU `mapResource`: 43 resources
- Radio `TransmitMessage`: 240
- Route/task actions: 703
- Added new audio to both locales
- Replaced newly added audio under the same `ResKey`
- Replaced an existing campaign radio resource while keeping the same `ResKey`
- Added briefing image, trigger image, kneeboard image, and Lua script
- Added Mission Start audio/script triggers
- Saved `.miz` reloaded successfully

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

## Important Git Note

Do not commit DCS mission payloads. `.miz` and `.zip` files are ignored intentionally because campaign missions can be large and may contain licensed/protected content.

The repository should contain source code and docs only. Local test missions belong in `work/`, which is also ignored.

## Current Limits

- Full visual editing of every DCS route/task action is not implemented yet.
- Classic `mission.trig` simple trigger creation is supported, but complex Mission Editor condition/action builders are still future work.
- In-game playback should be validated in DCS for final release builds. Offline tests verify archive structure, Lua table persistence, `dictionary`, `mapResource`, and file references.
