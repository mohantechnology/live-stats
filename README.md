# Taskbar Live Stats

Live CPU, memory, GPU, network, and disk stats embedded in the Windows taskbar.

## Download (Windows)

Every push to `main` builds downloadable Windows artifacts in GitHub Actions:

1. Open [Actions](https://github.com/mohantechnology/live-stats/actions)
2. Open the latest **Build Windows portable** run on `main`
3. Download **TaskbarLiveStats-win-x64-portable** (recommended)

That package is a **self-contained single-file EXE** — unzip (or use the `.exe` directly) and run on Windows 10/11 x64. No separate .NET install is required.

A smaller **framework-dependent** artifact is also uploaded; that one needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Tagged releases (`v*`) also publish the same files on the repo [Releases](https://github.com/mohantechnology/live-stats/releases) page.

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
