# AudioChoice Companion

The AudioChoice Companion is one .NET desktop application shared by macOS and Windows.

It signs in to the user's AudioChoice account, accepts an M4B or MP3, creates a short-lived encrypted transfer, and produces an `audiochoice://transfer/...` QR code. Scanning it in the Android app begins the existing import process and deletes the temporary handoff after the phone verifies the file.

When a user selects AAX, the companion records the same ownership acknowledgment used by the mobile app, then opens the configured external conversion site in the browser. When conversion is complete, the user returns to select the resulting M4B for transfer. The original AAX is not uploaded or transferred by the companion.

## Build packages

```sh
dotnet publish AudioChoice.Companion/AudioChoice.Companion.csproj -c Release -r osx-arm64 --self-contained true -o releases/macos-arm64
dotnet publish AudioChoice.Companion/AudioChoice.Companion.csproj -c Release -r win-x64 --self-contained true -o releases/windows-x64
```

Run the produced `AudioChoiceCompanion` executable. It opens a private local companion page at `http://127.0.0.1:47621`.
