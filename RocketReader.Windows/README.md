# Rocket Reader Windows

This is a Windows-native WPF port of the RSVP prototype in `RocketReaderApp.swift`.

Current feature set:

- ORP-aligned word display with the pivot letter highlighted
- Warm-up ramp over the first 40 words
- Punctuation-aware and word-length-aware pause multipliers
- Adjustable reading speed from 150 to 1200 WPM
- Optional metronome click
- Import from TXT and PDF files
- Recent text persistence and last-session WPM restore
- Red-dot app icon for the EXE and running window
- Release publishing as a self-contained single-file executable

Run locally:

```powershell
dotnet run --project .\RocketReader.Windows
```

Publish a single EXE:

```powershell
dotnet publish .\RocketReader.Windows -c Release
```

Published output:

- `.\RocketReader.Windows\bin\Release\net8.0-windows\win-x64\publish\RocketReader.Windows.exe`