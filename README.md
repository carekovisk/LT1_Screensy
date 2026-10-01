101% AI


Screensy App Audio is a lightweight Windows wrapper around Screensy that lets you share your screen with audio from one specific application only. Your voice calls, notifications and everything else playing on your PC stay out of the stream.

Browsers like Edge and Chrome can only capture audio from a single tab or from the entire system. Screensy App Audio closes that gap with the Windows WASAPI Process Loopback API, which captures audio from a chosen process and its child processes. It then injects that audio into the page as a regular audio track. Screensy itself is untouched, and viewers don't need to install anything.

Features

Stream audio from a single app (player, game, browser, etc.)
"Everything except" mode, e.g. share all system audio but leave out Discord or Teams
Switch the captured app mid-stream without restarting the share
No virtual audio cables, drivers or third-party software
Single self-contained .exe, no .NET runtime required to run

Requirements: Windows 10 version 2004 (build 19041) or later and the WebView2 Runtime. Building it requires the .NET 8 SDK; just run build.bat
