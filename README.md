98% vibe coded


# Lightone Stream

A minimal Windows wrapper around **https://screensy.marijn.it/** that uses Microsoft Edge WebView2 and changes the screen-capture request before Screensy sees it.

Default mode:

- `windowAudio: "window"`
- `systemAudio: "exclude"`

The goal is to share a selected application window **with that window's audio**, without sending Discord/system audio into the Screensy stream.

## Requirements

- Windows 10/11 x64 (ARM64 is also supported by the build script)
- Microsoft Edge WebView2 Runtime **141 or newer**
- Internet access to use Screensy

The published EXE is self-contained for .NET, so the target machine does **not** need the .NET Desktop Runtime installed.

## Build

Double-click:

```text
build.bat
```

If .NET 8 SDK is not installed, the script downloads Microsoft's official `dotnet-install.ps1` and installs a private SDK under `.dotnet` inside this project folder.

Output:

```text
dist\win-x64\Lightone-Stream.exe
```

For Windows on ARM:

```text
build.bat arm64
```

## Usage

1. Run `Lightone-Stream.exe`.
2. Leave **Audio: Window only** selected.
3. Click Screensy's normal **Start sharing** button.
4. In the WebView2/Edge picker, select **Window** and then the game/application.
5. Click **Copy Share Link** in the wrapper toolbar.
6. Paste that link to your friends. It includes the Screensy room ID in the URL fragment (for example `https://screensy.marijn.it/#LargeRobotsPlayQuickly`).

Do **not** select Entire Screen if you want application-only audio. With `systemAudio: "exclude"`, full-screen sharing intentionally won't include system audio.

## Share link

Screensy stores the room ID in the URL fragment (`#RoomName`). The wrapper's **Copy Share Link** button copies the current full Screensy URL directly to the Windows clipboard.

The button becomes usable as soon as Screensy has generated its room name. You can copy the link before or after starting screen capture; your friends use the same link in a normal browser.

## Audio modes

- **Window only**: `audio: true`, `windowAudio: "window"`, `systemAudio: "exclude"`.
- **System audio**: allows system audio again.
- **No audio**: disables capture audio.

Changing the mode reloads Screensy so the capture hook is installed before the site code executes.

## What the wrapper changes

The app injects a script at document creation time using WebView2's `AddScriptToExecuteOnDocumentCreatedAsync`. It wraps only `navigator.mediaDevices.getDisplayMedia()` and only when the loaded hostname is `screensy.marijn.it`.

It does **not** proxy or relay the stream. Screensy's normal WebRTC/P2P behavior is left intact.

## Limitations

`windowAudio` is a browser/platform capability and is still subject to what Chromium/WebView2 and Windows can capture from the selected application. Some protected/DRM content, exclusive audio modes, unusual multi-process apps, or apps that output audio from a different process may not expose audio as expected.

This wrapper is intentionally small. It does not modify or redistribute Screensy's source code; it loads the public Screensy website and alters the browser capture options locally.
