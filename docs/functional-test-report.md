# Functional Test Report

Date: 2026-06-04

All tests used copies of real DCS campaign missions. Original missions under `D:\steam\steamapps\common\DCSWorld\Mods\campaigns` were not modified.

## Builds

```powershell
dotnet build "F:\mizedit-main\mizedit c#.sln"
dotnet build "F:\mizedit-main\mizedit c#.sln" -c Release
```

Result: both builds passed with 0 errors.

## M04.miz Smoke Test

Source:

```text
D:\steam\steamapps\common\DCSWorld\Mods\campaigns\MAD AH-64D\M04.miz
```

Findings:

- Locales: `DEFAULT`, `RU`
- Theatre: `PersianGulf`
- `ext_loader.library`: `Mods/campaigns/MAD AH-64D/MADAH64D`
- `miz_id`: `1fd613bb193bfee204d17bce33be78b4`
- Default `mapResource`: 109 entries
- RU `mapResource`: 0 entries, falls back to `DEFAULT`
- Briefing pictures: 4
- Classic triggers: 0
- Route/task actions: 492
- Radio `TransmitMessage`: 79

Save/reload result:

- Saved copy was readable.
- Locales remained `DEFAULT`, `RU`.
- `miz_id` was preserved.
- Radio `TransmitMessage` count remained 79.

## A-10C Outpost Stress Test

Source:

```text
D:\steam\steamapps\common\DCSWorld\Mods\campaigns\A-10C Outpost\M3OUTPOSTA10.miz
```

Baseline:

- Locales: `DEFAULT`, `RU`
- Theatre: `Syria`
- `ext_loader.library`: `Mods/campaigns/A-10C Outpost/A-10C-II-Outpost`
- Default `mapResource`: 276 entries
- RU `mapResource`: 43 entries
- Briefing pictures: 5
- Classic triggers: 0
- Route/task actions: 703
- Radio `TransmitMessage`: 240

Actions tested:

- Changed `DEFAULT` and `RU` briefing descriptions.
- Changed `DEFAULT` and `RU` sortie text.
- Added new audio in `DEFAULT` and `RU`.
- Replaced newly added audio while keeping the same `ResKey`.
- Added and replaced briefing image in both locales.
- Added briefing picture reference to mission table.
- Added trigger picture reference to mission table.
- Added Lua script to `l10n/DEFAULT`.
- Added Mission Start trigger to play added audio through `getValueResourceByKey`.
- Added Mission Start trigger to run added script through `getValueResourceByKey`.
- Added kneeboard page under `KNEEBOARD/IMAGES`.
- Saved copy and reloaded it.

Reload result:

- Radio `TransmitMessage`: 240 -> 240
- Route/task actions: 703 -> 703
- Classic triggers: 0 -> 2
- Briefing pictures: 5 -> 6
- Default `mapResource`: 276 -> 279
- RU `mapResource`: 43 -> 45
- Added audio files existed in both locales.
- Added picture files existed in both locales.
- Added script existed in `l10n/DEFAULT`.
- Added kneeboard image existed in the archive.
- Mission text contained the new audio and script trigger actions.

Existing resource replacement test:

- Existing radio key: `ResKey_advancedFile_31`
- Original file: `М2-1-1ENG-radio.ogg`
- Replaced file: `mizedit_custom_beep_replacement.wav`
- `TransmitMessage` still referenced `ResKey_advancedFile_31`
- Radio count remained 240

This confirms that replacing a file behind an existing resource key does not break existing mission references.
