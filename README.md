# UTM4WIN

Native Windows 11 virtual machine manager, built with C#/.NET 10 and WinUI 3.
M0 feasibility is accepted; M1 provides the application foundation and an empty library shell.
VM creation, persistence and virtualization are later milestones in [PLAN.md](PLAN.md).

## Development

Use Windows 11 x64 and the SDK pinned in global.json (10.0.301, compatible patch roll-forward).
Windows App SDK 2.5.1 and Windows SDK BuildTools 10.0.26100.9169 are pinned centrally;
NuGet uses nuget.org and each project tracks packages.lock.json.

```powershell
dotnet restore UTM4WIN.sln --locked-mode
dotnet build UTM4WIN.sln --no-restore
dotnet test UTM4WIN.sln --no-build
./tools/Start-App.ps1 -NoBuild
```

On the qualified host, PATH's dotnet lacks the SDK; Rider has a private SDK under
%LOCALAPPDATA%/Programs/Rider/lib/ReSharperHost/windows-x64/dotnet.
Start-App.ps1 chooses that installation when present, or accepts -DotNetPath.
It builds the default Debug app unless -NoBuild is supplied, then starts the apphost directly
and waits for the window's process; this avoids the CLI run exit-wait issue observed on this host.
For other CLI commands, invoke its dotnet.exe explicitly if needed. The launcher sets
only process environment variables and restores them when the app exits.

In Rider, open UTM4WIN.sln and run/debug the UTM4WIN.App .NET project configuration.
The SDK is selected under Build, Execution, Deployment. The unpackaged app targets
net10.0-windows10.0.26100.0, minimum Windows 11 build 22000 and runtime win-x64.
Both Any CPU and x64 solution configurations currently produce an x64 app.
Clean-machine installation and packaging remain M8 work.

## Structure

- src/UTM4WIN.Domain: independent domain assembly; business models begin in M2.
- src/UTM4WIN.Application: domain reference and future application use cases.
- src/UTM4WIN.Infrastructure: Windows persistence/OS adapter boundary.
- src/UTM4WIN.Virtualization.Qemu: Windows backend boundary.
- src/UTM4WIN.App: WinUI views, Toolkit MVVM state and DI composition.
- tests/UTM4WIN.UnitTests: Windows unit tests; no guest or QEMU is required.

The first four assemblies contain project boundaries only. The app implements library/settings
navigation and System/Light/Dark appearance for the current session. It explicitly declares
PerMonitorV2; actual higher/mixed-DPI guest input remains a mandatory M5 gate.
Structured ILogger output currently goes to the debugger; persistent bounded logs are later work.

Packages: CommunityToolkit.Mvvm 8.4.2, Microsoft DI/Debug logging 10.0.9, MSTest 4.1.0
and Microsoft.NET.Test.Sdk 18.0.1. NuGet metadata was checked for MIT licenses and compatible
.NET targets before restoration. The Toolkit and test framework use compatible .NET 8 targets;
restore, compilation and runtime tests verify the selected .NET 10 combination.
The Windows App SDK dependency/license graph was qualified in M0.
QEMU/VNC packages, executables, ISO files and reference source are not part of the application.

reference/ remains read-only analysis material. Git staging/commits are managed by the user.

