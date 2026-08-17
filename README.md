# Rabbit

Rabbit is a Windows-native RSVP speed reading prototype based on a Swift iOS proof of concept and the classic Rapid Serial Visual Presentation reading model.

This repository contains a WPF desktop app that presents one word at a time using ORP-style fixation, punctuation-aware timing, adjustable reading speed, and optional warm-up pacing.

## Features

- Native Windows desktop app built with C# and WPF
- ORP-aligned word rendering with a highlighted pivot character
- Adjustable reading speed from 150 to 1200 WPM
- Warm-up ramp for the first 40 words
- Pause multipliers for punctuation and longer words
- PDF, TXT, and EPUB import
- Website URLs can be pasted into the text field and are fetched into readable text before the reader starts
- Recent text persistence
- Fullscreen toggle with `F11` and exit with `Escape`
- Single-file release packaging

## Project Structure

- `Rabbit.Windows/` - the Windows application project

## Run Locally

```powershell
Set-Location 'C:\Projects\Rabbit'
dotnet build .\Rabbit.Windows\Rabbit.Windows.csproj
.\Rabbit.Windows\bin\Debug\net8.0-windows\Rabbit.exe
```

## Publish A Single EXE

```powershell
Set-Location 'C:\Projects\Rabbit'
dotnet publish .\Rabbit.Windows -c Release
```

Current release:

- Version `0.1.5`
- Standalone EXE: `Rabbit.Windows\bin\Release\net8.0-windows\win-x64\publish\Rabbit-v0.1.5-win-x64.exe`

Published output:

- `Rabbit.Windows\bin\Release\net8.0-windows\win-x64\publish\Rabbit-v0.1.5-win-x64.exe`

The versioned EXE is the release artifact to keep, while the unversioned host exe is just an intermediate publish output.

## Notes

- The release build is self-contained, so the published EXE includes the .NET runtime.
- PDF extraction currently relies on `UglyToad.PdfPig`.
- EPUB files are read from the package spine and converted to plain text for RSVP reading.
- Website URLs entered in the main text box are fetched and converted to readable page text before starting the reader.