# Otter Desktop

A small offline Windows desktop companion built from the Otter v2 sprite sheet.

Version 1.14 adds persistent animation-speed control from 50% to 200%. Frame playback speed and active interaction duration update together; movement speed is unchanged. The six speed presets are available from the right-click menu. It retains the illustrated golden Zzz cluster in the curled sleeping loop, the guided animation rules, corrected idle frame registration, and repaired running-left row. Ball and snack play, screen-sharing privacy, and the soft ground shadow remain available.

## Controls

- Left-drag Otter to reposition it. An awake Otter runs in the drag direction; a sleeping Otter stays asleep during and after dragging.
- Click Otter to wake it and give a small wave. If a meeting or fullscreen context remains active, Otter returns to sleep after the wave.
- Right-click Otter for actions, size, pause, always-on-top, startup, reset, and exit.
- Choose **Animation speed** from the right-click menu to set frame playback from 50% to 200%; the selection is saved between launches.
- Use **Play together** to toss a ball or give Otter a fish snack.
- Use **Work companion** to control screen-sharing privacy, automatic or manual meeting mode, fullscreen quiet mode, typing reactions.
- Teams meetings, manual meeting mode, fullscreen apps, Windows presentation mode, and PowerPoint slide-show windows trigger breathing sleep. Sleep takes priority over typing, but explicit clicks and play actions still work.
- While typing when awake, Otter stays in place and holds a looking-down pose without looping. Idle resumes shortly after typing stops.
- After five minutes without keyboard or mouse activity, Otter sleeps until clicked or given an explicit pet action. Ordinary keyboard/mouse activity and dragging do not wake it.
- Manual meeting mode starts off on each app launch, even if it was enabled when you last exited.
- Otter does not wander or perform unrequested waves, jumps, reviews, or other automatic actions. Toy travel only happens when requested.
- When idle, Otter looks toward the mouse pointer.
- Dragging works across multiple monitors.

## Privacy and portability

Otter Desktop makes no network requests and does not use ChatGPT. It never records key codes or typed text: the typing reaction stores only the time of the latest key press. Inactivity detection reads only Windows' time since the last keyboard or mouse input. Teams detection is performed locally using visible Teams controls and Windows' local microphone-use status.

Screen-sharing protection uses Windows' capture-exclusion feature for the pet, toys, and pet menus. It is enabled by default and can be changed from the Work companion menu. When Teams sharing is detected, Otter and its accessories also disappear from your desktop and return when sharing ends. Any active play sequence is cancelled rather than continuing invisibly.

Capture exclusion requires compatible Windows and capture software; it is not a security guarantee. Teams detection is best effort, depends on the Teams UI/version/language and corporate policies, and is checked approximately every two seconds. Check a test share before an important presentation. If capture exclusion is unavailable, the work-status menu reports it. Exit Otter for guaranteed absence when using unsupported capture software.

Meeting and fullscreen modes show Otter curled up asleep with gentle breathing but no autonomous movement. Clicks, dragging, toys, and snacks remain available, and Otter returns to sleep after an explicit action while the work context remains active. Dragging a sleeping Otter does not wake it. A manual meeting-mode switch is included for managed computers where automatic Teams inspection is restricted; it starts off on every launch and makes Otter sleep, not disappear. Fullscreen detection also recognizes Windows presentation mode and PowerPoint slide-show windows.

The portable `OtterDesktop.exe` includes the Windows .NET runtime, so it does not require an installer or administrator access. Settings are stored in `%LOCALAPPDATA%\OtterDesktop`.

Download the latest `OtterDesktop-windows-x64.zip` from the [GitHub Releases page](https://github.com/JorgePaiva-NTT/desktop-otter/releases/latest). Extract the ZIP and run `OtterDesktop.exe`.

Some managed work computers block unsigned applications. If Windows SmartScreen appears, your organization's policy determines whether you can run the app. The app does not attempt to bypass those policies.

## Build and verify

With a .NET SDK that supports `net9.0-windows` and PowerShell 7 on Windows:

```powershell
dotnet build OtterDesktop.csproj -c Release
pwsh -NoProfile -STA -File Tools/Test-Companion.ps1
```

The smoke suite uses temporary settings and checks the held typing pose, breathing sleep and priority, the five-minute inactivity threshold, click-only waking, sleeping/awake dragging, startup mode reset, absence of autonomous actions, ball/snack completion, pause cancellation, sharing hide/restore, Windows capture-affinity flags, and sprite rendering. Teams state and input-idle durations are simulated for these checks; a live Teams screen-share test must be performed separately.

To regenerate the sleeping artwork with Python and Pillow:

```powershell
python Tools/create_sleep_frames.py
```

The generator appends eight 192x208 frames at row 11, regenerates each running-left cell from its clean right-running counterpart, and writes a contact sheet to `obj/sleeping-preview.png`. Run it after normalizing a source atlas if regenerating all assets.

To publish a downloadable release, update the project version and push a matching tag such as `v1.14.0`. GitHub Actions creates a self-contained Windows x64 ZIP and SHA-256 checksum on the Releases page.
