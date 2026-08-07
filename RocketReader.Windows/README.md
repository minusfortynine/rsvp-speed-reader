# Rocket Reader Windows

This is a Windows-native WPF port of the RSVP prototype in `RocketReaderApp.swift`.

Current feature set:

- ORP-aligned word display with the pivot letter highlighted
- Warm-up ramp over the first 40 words
- Punctuation-aware and word-length-aware pause multipliers
- Adjustable reading speed from 150 to 1200 WPM
- Optional metronome click
- Import from TXT, PDF, and EPUB files
- Paste a website URL into the text field to fetch readable page text before starting the reader
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

Current release:

- Version `0.1.3`
- Standalone EXE: `.\RocketReader.Windows\bin\Release\net8.0-windows\win-x64\publish\RocketReader-v0.1.3-win-x64.exe`

Published output:

- `.\RocketReader.Windows\bin\Release\net8.0-windows\win-x64\publish\RocketReader-v0.1.3-win-x64.exe`

The release build is self-contained and publishes as a single-file executable; keep the versioned EXE as the distributable file.

Website URLs entered in the main text box are converted to readable page text before the reader starts.