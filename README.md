# Farm Timer Overlay

Windows 10/11 x64 overlay timer for a repeating 2-minute farming loop.

## Default cycle

- FARM: 01:45
- LOOT: 00:15
- Total cycle: 02:00, repeats automatically

## Features

- Always-on-top compact overlay
- Click-through locked mode
- Ctrl + Alt + F9 toggles Custom mode
- Draggable overlay in Custom mode
- Adjustable FARM / LOOT split
- Modern two-phase progress preview
- Red pre-LOOT warning during the final 5 seconds of FARM
- Sound Alert dropdown with independent Pip and Ting-ting toggles
- In-app Sound Alert volume control (0-100%)
- Soft pip synced to each red pulse during the final 5 seconds of FARM
- Double ting-ting when LOOT begins
- Custom rounded Opacity and Volume sliders matching the FARM/LOOT split control
- Tray controls for pause/resume, reset and exit
- Close button (X) and Alt+F4 fully exit the app instead of hiding it
- Settings saved under %LOCALAPPDATA%\FarmTimerOverlay
- Single-instance behavior
- Countdown UI refreshes only while running (no 80 ms idle update loop while paused)

## Portable build

The GitHub Release contains a single self-contained Farm Timer Overlay.exe for Windows x64.

It includes the .NET 8 runtime and does not require a separate .NET installation.

## Build

Project: FarmTimerOverlay.Portable.csproj

Target: net8.0-windows / win-x64 / self-contained / single-file

## Regression checks

Run the PowerShell script tests/Verify-Settings.ps1 on Windows.
The script builds the app using the .NET Framework C# compiler and checks settings
parsing, timer/sound boundary values, and paused/active/reset UI tick behavior
without modifying your saved app settings.
GitHub Actions runs the same checks for commits and pull requests to main.
