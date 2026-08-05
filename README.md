# Rocket Reader

Rocket Reader is a Windows-native RSVP speed reading prototype based on a Swift iOS proof of concept and the classic Rapid Serial Visual Presentation reading model.

This repository contains a WPF desktop app that presents one word at a time using ORP-style fixation, punctuation-aware timing, adjustable reading speed, and optional warm-up pacing.

## Features

- Native Windows desktop app built with C# and WPF
- ORP-aligned word rendering with a highlighted pivot character
- Adjustable reading speed from 150 to 1200 WPM
- Warm-up ramp for the first 40 words
- Pause multipliers for punctuation and longer words
- PDF and TXT import
- Recent text persistence
- Fullscreen toggle with `F11` and exit with `Escape`
- Single-file release packaging

## Project Structure

- `RocketReader.Windows/` - the Windows application project
- `RocketReaderApp.swift` - the original Swift prototype reference

## Run Locally

```powershell
Set-Location 'c:\Users\adamczra\RSVP Speed Reader'
dotnet build .\RocketReader.Windows\RocketReader.Windows.csproj
.\RocketReader.Windows\bin\Debug\net8.0-windows\RocketReader.Windows.exe
```

## Publish A Single EXE

```powershell
Set-Location 'c:\Users\adamczra\RSVP Speed Reader'
dotnet publish .\RocketReader.Windows -c Release
```

Published output:

- `RocketReader.Windows\bin\Release\net8.0-windows\win-x64\publish\RocketReader.Windows.exe`

## Notes

- The release build is self-contained, so the published EXE includes the .NET runtime.
- PDF extraction currently relies on `UglyToad.PdfPig`.
