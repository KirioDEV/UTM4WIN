# UTM4WIN

Native Windows 11 virtual machine manager, built with C#/.NET 10 and WinUI 3.
M0 feasibility is accepted; M1 provides the native application foundation.
M2 adds a persistent VM metadata library and editor; M3 adds a creation wizard with
read-only installation media and a managed qcow2 disk. Installing and running guests
are later milestones in [PLAN.md](PLAN.md).

## Development

Use Windows 11 x64 and the SDK pinned in global.json (10.0.301, compatible patch roll-forward).
Windows App SDK 2.5.1 and Windows SDK BuildTools 10.0.26100.9169 are pinned centrally;
NuGet uses nuget.org and each project tracks packages.lock.json.

```powershell
dotnet restore UTM4WIN.sln --locked-mode
dotnet build UTM4WIN.sln --no-restore
dotnet test UTM4WIN.sln --no-build --filter "TestCategory!=QemuIntegration"
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

- src/UTM4WIN.Domain: immutable VM configuration, resource ownership and validation.
- src/UTM4WIN.Application: repository/creation/disk ports, revision snapshots and isolated drafts.
- src/UTM4WIN.Infrastructure: versioned JSON repository, library registration and creation coordination.
- src/UTM4WIN.Virtualization.Qemu: qualified qcow2 creation and owned Windows tool processes.
- src/UTM4WIN.App: WinUI views, Toolkit MVVM state and DI composition.
- tests/UTM4WIN.UnitTests: Windows unit tests and separately categorized QEMU integration tests.

The app implements library/settings navigation, metadata editing, a creation wizard and
System/Light/Dark appearance for the current session. It explicitly declares
PerMonitorV2; actual higher/mixed-DPI guest input remains a mandatory M5 gate.
Structured ILogger output currently goes to the debugger; persistent bounded logs are later work.

## Metadata library

Use **Add metadata** to save a name, CPU count, memory and explicit WHPX/TCG selection.
Select an entry to edit it. These entries describe the x86_64 Linux BIOS profile; they do
not contain a bootable guest yet. WHPX selection does not enable Windows features or assert
host availability. Machine alias `pc` is metadata only; a qualified versioned machine and
runtime resource checks must be established before launching in M4.

Default storage is `%LOCALAPPDATA%/UTM4WIN/Library`: `library.json` contains registered IDs;
`<vm-id>/vm.json` contains schema-1 configuration. The apphost also accepts
`--library-root <absolute local directory>` for a separate library. Remote/device paths
and paths through junctions/symlinks/reparse points are unsupported.

**Remove entry** asks for confirmation and removes only registration. Configuration,
disks and external resources remain intact. Retained UUID directories are listed separately;
**Register retained metadata** validates and re-registers an existing configuration.
Incomplete creation is retained for recovery; it is never silently registered or deleted.
If registration fails after saving metadata, discard the editor, reload and register the
retained entry. An empty/incomplete directory needs manual inspection; no cleanup is implicit.

Editors keep drafts and require explicit discard after changes. Concurrent saves check the
original SHA-256 revision and reject stale edits. A library-wide file lock serializes cooperating
instances; a busy library reports an I/O error rather than waiting indefinitely. Current metadata
operations do not start a VM; session/disk ownership guards must be added with M4.

Each write flushes a temporary sibling before move/replace. Replacement retains one `.previous`
version. This is a single-file commit, not a transaction across configuration and index or a
guarantee against power loss. Unknown newer schemas and unsupported older schemas fail clearly
without automatic migration, downgrade or backup restoration. Schema 1 is the first format;
there is no released older format to migrate. Corrupt metadata is reported; a corrupt index
blocks mutations and requires explicit recovery from its retained previous version.

Limits: 1000 registered entries, 1 MiB per metadata document, 16 resources, 1–64 CPUs,
128–262144 MiB memory. Owned resource paths use `disks/<filename>`; external paths are local,
absolute and read-only. Referenced files must exist and be readable. M2 checks metadata and
access, not actual disk contents/backing chains, firmware compatibility or runtime capacity.
Filesystem checks reject observed reparse points; they are not a security boundary against
hostile local path replacement or hard-link aliases. Metadata operations never delete a disk.

## Create a VM

Use **Create VM** and complete profile/hardware, installation media/storage and review.
The initial profile is Linux x86_64 with BIOS. Select a local Linux installation ISO and
`qemu-img.exe` from the qualified QEMU 11.1.0 build. Native file pickers or direct paths
are supported. No QEMU executable is bundled or downloaded by the application.

The qualified executable SHA-256 is pinned in QemuImgDiskService; another 11.1.0 build
must be reviewed before use. The QEMU installation and its DLLs must be trusted. The image
tool path is remembered only after successful creation and only for this app session.
ISO 9660 volume descriptors are checked; this does not authenticate the ISO or prove its
guest architecture/bootability. UDF-only media and other firmware/guest profiles are unsupported.

Choose 1–2048 GiB of virtual capacity. Creation makes a sparse qcow2 1.1 image without a
backing file, external data file or encryption, then checks its format/capacity/dependencies
with the real image tool. Sparse creation does not reserve enough space for all future guest
writes. Existing destination files are rejected with CreateNew; they are never truncated.
The ISO stays external and is held read-only during creation, without copying or deletion.

Review explicitly authorizes removal of this operation's incomplete disk on cancellation
or failure. **Cancel creation** waits for the owned tool to stop and cleanup to finish.
Only the new disk and empty directories from the current operation can be removed; unexpected
files are retained. Once configuration is committed, its disk is retained even if registration
fails or cancellation arrives. Close the wizard, reload and register the retained metadata.
An app crash can leave partial files needing manual inspection; startup never deletes them.

The tool is assigned atomically to a kill-on-close Windows Job Object before execution,
with a standard-handle allowlist and verified containment. This native boundary implements
the approved ownership requirement that ProcessStartInfo.ArgumentList cannot expose.
Arguments use tested Windows quoting, with no shell or arbitrary extra arguments. Each command
has a 30-second deadline and bounded output; uncertain cleanup retains the disk rather than
deleting a file a process might still use. Library creation remains serialized by its file lock.

Creation prepares disk/configuration/library registration. VM launch, installation and
lifecycle controls begin with separately approved M4 work. Network configuration remains
disabled for now; integrated NAT/console qualification belongs to M5.

Ordinary unit tests use inert fixtures and require no QEMU or guest. Run the explicit integration
suite only with the approved binary; it creates disposable new images in a unique temp directory:

```powershell
$env:UTM4WIN_TEST_QEMU_IMG = 'C:\path\to\approved\qemu-img.exe'
dotnet test UTM4WIN.sln --no-build --filter "TestCategory=QemuIntegration"
```

No real guest is started by those tests. Without an explicit tool path they are inconclusive;
keep the category filter for an ordinary unit-only run.

Packages: CommunityToolkit.Mvvm 8.4.2, Microsoft DI/Debug logging 10.0.9, MSTest 4.1.0
and Microsoft.NET.Test.Sdk 18.0.1. NuGet metadata was checked for MIT licenses and compatible
.NET targets before restoration. The Toolkit and test framework use compatible .NET 8 targets;
restore, compilation and runtime tests verify the selected .NET 10 combination.
The Windows App SDK dependency/license graph was qualified in M0.
QEMU/VNC packages, executables, ISO files and reference source are not part of the application.

reference/ remains read-only analysis material. Git staging/commits are managed by the user.

