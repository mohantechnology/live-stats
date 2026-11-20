# Taskbar Live Stats

Live CPU, memory, GPU, network, and disk stats embedded in the Windows taskbar.

## Download (Windows)

**Recommended (portable, no .NET install):**

- [Download portable ZIP](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-portable.zip)
- [Download portable EXE](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-portable.exe)

These links always point at the latest successful `main` build ([continuous release](https://github.com/mohantechnology/live-stats/releases/tag/continuous)). Unzip if needed, then run on Windows 10/11 x64.

Smaller build (needs [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)):

- [Framework-dependent ZIP](https://github.com/mohantechnology/live-stats/releases/download/continuous/TaskbarLiveStats-win-x64-fd.zip)

Versioned releases: [Releases](https://github.com/mohantechnology/live-stats/releases) (created when you push a `v*` tag).

### How to view artifacts on GitHub

**Option A — Continuous release (easiest for users)**  
Open [Releases → Continuous build](https://github.com/mohantechnology/live-stats/releases/tag/continuous) and download the `.zip` / `.exe` assets.

**Option B — From an Actions run**  
1. Open [Actions → Build Windows portable](https://github.com/mohantechnology/live-stats/actions/workflows/build-release.yml)  
2. Click the latest green run on `main`  
3. Scroll to the bottom **Artifacts** section  
4. Download **TaskbarLiveStats-win-x64-portable** (zip of the EXE)

Actions artifacts expire after 30 days. The continuous release links above stay available and are what the README points to.

## Requirements

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build
- For framework-dependent publish: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Install .NET 10 SDK

From PowerShell:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Open a new terminal after install, then confirm the SDK:

```powershell
dotnet --version
```

The Desktop Runtime is included with the SDK. For a machine that only runs the framework-dependent build:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

## Build and run (development)

From the repo root (`d:\dev_software\taskbar-live-stats`):

```powershell
dotnet build .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release -f net10.0-windows
dotnet run --project .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release -f net10.0-windows --no-build
```

Or build and run in one step:

```powershell
dotnet run --project .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release -f net10.0-windows
```

Stop the app from the tray icon (right-click → Exit) before rebuilding if the EXE is locked.

## Publish (portable EXE)

Self-contained single-file (no separate .NET install needed on the target PC):

```powershell
dotnet publish .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release /p:PublishProfile=Trimmed
```

Output:

```text
.\publish\TaskbarLiveStats.exe
```

Run it:

```powershell
.\publish\TaskbarLiveStats.exe
```

Smaller build that requires the .NET 10 Desktop Runtime:

```powershell
dotnet publish .\src\TaskbarLiveStats\TaskbarLiveStats.csproj -c Release /p:PublishProfile=FrameworkDependent
```

Output:

```text
.\publish-fd\TaskbarLiveStats.exe
```

## How GPU % is calculated

- Source: Windows PDH `\GPU Engine(*)\Utilization Percentage`
- Per-process engine samples are **summed per physical engine**, then the **busiest engine** on the primary GPU is used (same approach as Task Manager)
- Percentages are **rounded up** (ceiling) so small load does not display as `0%`
- Failed/empty PDH reads keep the last good GPU value instead of flashing zero
