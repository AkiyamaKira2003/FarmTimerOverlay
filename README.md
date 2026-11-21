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
- Sound Alert master switch mutes both automatic warning pips and LOOT chimes
- In-app Sound Alert volume control (0-100%)
- Soft pip synced to each red pulse during the final 5 seconds of FARM
- Double ting-ting when LOOT begins
- Custom rounded Opacity and Volume sliders matching the FARM/LOOT split control
- Tray controls for pause/resume, reset and exit
- Close button (X) and Alt+F4 fully exit the app instead of hiding it
- Settings saved under %LOCALAPPDATA%\FarmTimerOverlay
- Settings writes stage to a same-folder temporary file and replace the INI
  only after flushing, preserving the previous complete file if staging or
  the pre-commit step fails; interrupted power-loss durability is not certified
- Single-instance behavior
- Countdown UI refreshes only while running (no 80 ms idle update loop while paused)
- Restores an off-screen overlay to the primary work area if its saved position
  falls into a gap between offset displays; checks individual monitor working
  areas in screen pixels rather than the virtual desktop's bounding box

## Portable build

The GitHub Release contains a single self-contained Farm Timer Overlay.exe for Windows x64.

New source builds target supported **.NET 10 LTS** after .NET 8 reached end of support
on November 10, 2026. The existing released executable is **not** automatically upgraded;
release assets must be rebuilt and verified separately. Self-contained builds do not
require a separate .NET runtime installation.

## Build

Project: FarmTimerOverlay.Portable.csproj

Target: net10.0-windows / win-x64 / self-contained / single-file

## Regression checks

Run the PowerShell script tests/Verify-Settings.ps1 on Windows.
The script compiles into a fresh temporary directory (never replacing the
portable EXE), loads that test assembly without locking the file, and checks
settings parsing, timer/sound boundaries, paused/active/reset UI tick behavior
and master/individual sound toggle gating. Its alert-state checks do not open a
WPF window, play audio or write to your saved app settings.
Settings persistence tests use a uniquely owned temporary directory to verify
failed-save preservation, replace/create, and a real Save()/reload roundtrip.
Synthetic multi-monitor geometry tests also cover staggered monitors, negative
screen origins and partially visible windows. These tests validate the
visibility decision only; interactive multi-monitor / mixed-DPI visual QA is
still required before an installer or release is declared accepted.
GitHub Actions runs the same checks for commits and pull requests to main,
then independently publishes the actual .NET 10 Windows x64 self-contained WPF
application and validates the generated PE executable. It does not run the GUI
or modify an existing GitHub Release. Successful CI runs retain **only the
verified EXE** as a seven-day downloadable Actions artifact for manual
Windows acceptance; this is an unsigned build, not a release or an installed
update. Compare its SHA-256 to the exact CI run log before testing. Do not
assume source compilation or PE validation proves interactive GUI behavior.
