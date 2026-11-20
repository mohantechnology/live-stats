# Taskbar Live Stats

Live system stats on your Windows taskbar — CPU, memory, GPU, network, and disk — always visible while you work.

Runs from the system tray as a portable app. No installer required.

## Features

### Live metrics
- **CPU, Memory (RAM), GPU, VRAM** — utilization at a glance
- **Upload / Download** — live network speeds
- **Disk read / Disk write** — live storage speeds
- **NPU** (and NPU memory) when your PC reports one
- Optional **busiest cores / GPU engines** next to CPU and GPU (handy on hybrid CPUs)
- Per-metric **hide when idle** — unused meters stay out of the way
- Optional **totals** for network and disk (since reboot or since last startup)
- Speeds in **bytes/s** or **bits/s**

### Where it lives
- **Taskbar mode** — sits near the notification area
- **Floating mode** — free overlay you can drag and snap to screen edges
- **Horizontal or vertical** layout
- **Compact or Default** density, custom fonts, opacity, and colors
- Colors that change with load (**idle / normal / high** thresholds)

### Usage history
- Charts for **network**, **disk**, and **computer awake time**
- Ranges from **15 minutes** up to a **year**, plus custom dates
- Period totals above each chart
- History stored locally on your PC (nothing is uploaded)

### Tray & convenience
- Lives in the **notification area** (system tray)
- **Left-click** tray icon → open Settings
- **Right-click** tray or stats bar → Settings, show/hide stats, Always on top, Task Manager, Autostart, Exit
- Keyboard shortcut **Win+Ctrl+U** to show/hide the live bar (can be disabled)
- **Autostart with Windows** (on by default on first run)
- Single instance — launching again focuses the existing app

## Download (Windows)

**Recommended — portable, no .NET install:**

- [Download portable ZIP](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-portable.zip)
- [Download portable EXE](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-portable.exe)

1. Download the ZIP or EXE above  
2. Unzip if needed  
3. Double-click `TaskbarLiveStats.exe`  
4. Look for the tray icon and the live stats bar near the taskbar

These links always track the latest successful build from `main` ([continuous release](https://github.com/mohantechnology/live-stats/releases/tag/continuous)).

**Smaller build** (needs [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)):

- [Framework-dependent ZIP](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-fd.zip)

Versioned releases: [Releases](https://github.com/mohantechnology/live-stats/releases)

### Other ways to get the build

- **Releases page:** [Continuous build assets](https://github.com/mohantechnology/live-stats/releases/tag/continuous)
- **Actions artifacts:** [Build Windows portable](https://github.com/mohantechnology/live-stats/actions/workflows/build-release.yml) → open the latest green run → **Artifacts** at the bottom (kept for 30 days)

## Requirements

- **Windows 10 or 11** (x64)
- Portable download: nothing else
- Framework-dependent download: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

Settings are saved under `%AppData%\TaskbarLiveStats\`.

---

## Build from source (developers)

### Install .NET 10 SDK

```powershell
winget install Microsoft.DotNet.SDK.10
```

Confirm:

```powershell
dotnet --version
```

### Run

```powershell
dotnet run --project .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release -f net10.0-windows
```

Stop the app from the tray (right-click → Exit) before rebuilding if the EXE is locked.

### Publish portable EXE

Self-contained (no .NET on the target PC):

```powershell
dotnet publish .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release /p:PublishProfile=Trimmed
```

Output: `.\publish\TaskbarLiveStats.exe`

Framework-dependent (smaller; needs Desktop Runtime):

```powershell
dotnet publish .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release /p:PublishProfile=FrameworkDependent
```

Output: `.\publish-fd\TaskbarLiveStats.exe`

### How GPU % is calculated

- Source: Windows PDH `\GPU Engine(*)\Utilization Percentage`
- Per-process engine samples are summed per physical engine; the **busiest engine** on the primary GPU is used (same idea as Task Manager)
- Values are rounded up so tiny load does not show as `0%`
- Failed reads keep the last good value instead of flashing zero
