# Green Line backend regression

`_FA-18C_Operation_Green_Line-RU_briefing_v2.zip` is the real-world regression fixture for MizEdit. It is intentionally ignored by Git because it is about 192 MB.

Run from the repository root:

```powershell
dotnet run --project .\tests\GreenLine.Integration\GreenLine.Integration.csproj -c Release
```

The runner works only on temporary copies. It validates all 12 `.miz` files by:

- opening the mission, every locale dictionary, and every `mapResource`;
- inventorying audio, images, and Lua scripts;
- opening and decoding the beginning of every OGG/WAV/MP3 with the same NAudio backend used by the application;
- decoding every PNG/JPEG/BMP with the same WPF imaging stack used by the application;
- saving to a new archive and reopening it;
- comparing every untouched ZIP entry byte-for-byte (the serialized `mission` entry is compared semantically);
- checking mission structure and resource counts before and after the round-trip;
- exercising locale add/delete, batch dictionary updates, resource add/replace/remove, briefing and trigger pictures, trigger creation, save, and reopen.
- exercising multiline-safe TXT export/import, batch processing, and mission state analysis.

Known fixture condition: `SYRIA M11 ZERO HELO.miz` contains `DEFAULT:ResKey_Action_1033 -> 11.17.ogg`, but that physical file is absent from the supplied package. The runner reports this as a source warning and verifies that MizEdit preserves the dangling entry unchanged.

The campaign missions use `ext_loader` (`Mods/campaigns/FA-18C Operation Green Line/...`) and contain empty embedded `trig`/`trigrules` tables. Therefore zero embedded triggers and radio actions is expected for the unmodified fixtures; the mutation scenario separately verifies MizEdit's trigger backend.
