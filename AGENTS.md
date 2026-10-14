# Repository Guidelines — UTM4WIN

## Project Overview

UTM4WIN is a native Windows virtualization application inspired by UTM for macOS.

The goal is to build a modern, performant, maintainable and user-friendly virtual machine manager for Windows 11.

UTM4WIN is an independent implementation, not a direct port or source-code copy of UTM.

Primary technologies:

- Language: C#
- Framework: .NET 10
- UI framework: WinUI 3
- Windows App SDK
- Architecture: MVVM
- IDE: JetBrains Rider
- Development agent: OpenAI Codex CLI
- Primary virtualization candidate: QEMU with WHPX acceleration
- Target OS: Windows 11 x64
- Potential future platform: Windows on ARM64

The final virtualization architecture must be validated during planning.

Do not assume that every feature available in UTM on macOS can be implemented identically on Windows.

## Language and Communication

- Communicate with the user in German.
- Write source code, identifiers, comments and technical documentation in English.
- Keep explanations clear and technically accurate.
- Explain important architectural decisions and trade-offs.
- Do not claim that something works unless it has been verified.
- Report limitations, missing dependencies and unresolved technical problems.
- Do not silently change the project's technology stack.

## Current Project Status

The repository initially contains an empty Rider solution.

The project is currently in the architecture and planning phase.

The first major deliverable is `PLAN.md`.

Until the user explicitly approves implementation:

- Do not create application projects.
- Do not implement application features.
- Do not install dependencies.
- Do not restructure the repository.
- Do not modify existing configuration files.
- Only create or update `PLAN.md` when requested.

After planning approval, implement features incrementally according to the approved plan.

Do not interpret approval of one development phase as permission to implement every subsequent phase.

## UTM Reference Source

The original UTM for macOS source code is located at:

`reference/UTM-main/`

Official upstream repository:

https://github.com/utmapp/UTM

This directory is reference material only.

Rules:

- Treat the entire reference directory as read-only.
- Never modify, delete, format or reorganize reference files.
- Never include the reference project in the .NET solution.
- Never attempt to compile the macOS application as part of UTM4WIN.
- Never copy UTM source files directly into the Windows implementation.
- Do not reproduce UTM branding, logos or proprietary assets.
- Respect upstream software licenses and third-party dependencies.
- Use the reference source to understand architecture, workflows and virtualization behavior.

When analyzing UTM, distinguish between:

1. UTM application logic.
2. QEMU functionality.
3. Apple Virtualization Framework functionality.
4. macOS-specific integrations.
5. Functionality realistically implementable on Windows.

Document important findings with relative source paths and relevant symbols.

Do not invent findings about source files that have not been inspected.

## Architecture Principles

Use a modular architecture with clear separation of responsibilities.

Expected architectural layers:

- Presentation: WinUI 3 views and view models.
- Domain: VM models, state and business rules.
- Application: VM lifecycle orchestration and use cases.
- Infrastructure: Configuration, persistence, logging and operating-system integration.
- Virtualization: Backend implementations and QEMU integration.

The exact project structure must be established in `PLAN.md`.

Prefer:

- MVVM
- CommunityToolkit.Mvvm
- Dependency injection
- Async/await
- CancellationToken
- Strongly typed models
- Interface-based abstractions
- Structured logging
- Testable services
- Explicit error handling

Avoid:

- Business logic inside XAML code-behind.
- God classes.
- Unnecessary static global state.
- Blocking UI-thread operations.
- Excessive abstractions.
- Premature optimization.
- Unnecessary external dependencies.
- Overengineering.

Implement only what is necessary for the current approved milestone.

## Coding Style and Naming Conventions

Follow modern Microsoft C# and .NET conventions.

- Use PascalCase for classes, interfaces, methods and public properties.
- Use camelCase for parameters and local variables.
- Prefix private fields with an underscore.
- Prefix interfaces with `I`.
- Use four spaces for indentation.
- Use K&R brace style in all application/test code and new code: place opening braces on the same line as the declaration or control statement, and keep `} else`, `} catch` and `} finally` on the same line. Follow the repository `.editorconfig`; never format `reference/` or generated build output.
- Enable nullable reference types.
- Prefer file-scoped namespaces.
- Prefer async APIs for I/O-bound operations.
- Use cancellation tokens for long-running operations.
- Use `IDisposable` or `IAsyncDisposable` for owned resources.
- Keep methods focused and understandable.
- Avoid unnecessary comments.
- Document non-obvious implementation decisions.

Use descriptive names rather than abbreviations.

Do not introduce a new coding convention without a clear reason.

## WinUI 3 Guidelines

The application must use native WinUI 3 with Windows App SDK.

Design goals:

- Native Windows 11 experience.
- Fluent Design.
- Dark and light themes.
- Responsive layouts.
- Accessible controls.
- Keyboard navigation.
- Consistent spacing and typography.
- Clear loading, empty and error states.

Use MVVM for UI state and application behavior.

Avoid putting virtualization logic directly into pages or windows.

Do not introduce WPF, Windows Forms, Avalonia, MAUI or Electron as a replacement for WinUI 3 without explicit user approval.

If a specific feature requires interoperability with another Windows technology, document the technical justification first.

## Virtualization Backend Guidelines

QEMU with Windows Hypervisor Platform acceleration is the initial preferred candidate.

The architecture should allow alternative backends in the future.

Investigate and document:

- QEMU process management.
- QEMU Monitor Protocol (QMP).
- WHPX availability and limitations.
- Hardware virtualization requirements.
- Guest architecture compatibility.
- VM configuration generation.
- Virtual disk creation and management.
- Networking.
- Guest display integration.
- Keyboard and mouse input.
- Graceful shutdown.
- Forced termination.
- VM state management.
- Error recovery.
- Snapshot capabilities.

Do not assume that WHPX accelerates all guest architectures.

Do not assume that Hyper-V management features are available on every Windows edition.

Never invent QEMU command-line options or protocol messages.

Validate backend behavior against official documentation and, when available, installed binaries.

## Process and Resource Safety

Virtualization software interacts with processes, files, disks and system resources.

Therefore:

- Never execute arbitrary user-controlled command strings through a shell.
- Prefer `ProcessStartInfo.ArgumentList` for process arguments.
- Validate executable paths.
- Validate disk-image and ISO paths.
- Handle process startup failures.
- Capture and log relevant process errors.
- Avoid orphaned QEMU processes.
- Clean up owned resources safely.
- Never delete a VM disk without explicit user authorization.
- Never overwrite an existing VM configuration without appropriate checks.
- Never silently discard unsaved configuration changes.
- Do not change Windows virtualization features or system settings without explicit approval.
- Do not download or execute external binaries without explicit approval.

Treat potentially destructive VM operations as high-risk actions.

## Repository Structure

Initial structure:

- `UTM4WIN.sln` — Rider/.NET solution.
- `AGENTS.md` — Agent instructions.
- `PLAN.md` — Architecture and development roadmap.
- `.codex/` — Local Codex configuration.
- `reference/UTM-main/` — Read-only upstream reference.

Potential future structure:

- `src/` — Application source code.
- `tests/` — Automated tests.
- `docs/` — Additional technical documentation.
- `assets/` — Application-owned assets.

Do not create future directories merely because they appear in this document.

The approved development plan determines when they are needed.

## Planning Workflow

Before implementing a substantial feature:

1. Read `AGENTS.md`.
2. Read `PLAN.md` if it exists.
3. Inspect relevant existing code.
4. Identify the current approved milestone.
5. Explain significant technical uncertainties.
6. Implement only the requested scope.
7. Run relevant validation.
8. Summarize the results.

Keep `PLAN.md` synchronized with meaningful architectural decisions and completed milestones when authorized.

Do not mark tasks complete unless their acceptance criteria are satisfied.

If a requirement is ambiguous and materially affects architecture or safety, ask the user before proceeding.

## Build and Development Commands

The solution initially contains no application projects.

Do not assume that build or test commands will succeed until projects exist.

After project creation, expected commands may include:

- `dotnet --info` — Inspect installed SDKs.
- `dotnet restore UTM4WIN.sln` — Restore dependencies.
- `dotnet build UTM4WIN.sln` — Build the solution.
- `dotnet test UTM4WIN.sln` — Run available tests.

Use these commands only when appropriate for the current development phase.

WinUI 3 may require additional Windows SDK, Windows App SDK, MSBuild or Visual Studio Build Tools components.

Verify the actual installed environment.

Do not assume that Rider provides every WinUI designer, packaging or deployment capability available in Visual Studio.

## Testing Guidelines

Every implemented feature should have appropriate validation.

Prefer automated tests for:

- VM configuration validation.
- VM lifecycle state transitions.
- QEMU argument generation.
- QMP message handling.
- Persistence and serialization.
- Error handling.
- Backend abstractions.

Use mocks or test doubles where practical.

Do not require a real VM for ordinary unit tests.

Integration tests requiring QEMU, WHPX or a specific Windows configuration must be clearly identified.

Never run destructive integration tests against real user VM images.

For UI changes, manually verify relevant navigation, layout, keyboard behavior and error states when possible.

Do not claim that tests passed unless they were actually executed.

## Dependencies and Licensing

Prefer maintained, well-documented dependencies.

Before introducing a significant dependency:

- Verify that it is compatible with the target framework.
- Check its license.
- Check its maintenance status.
- Explain why it is necessary.
- Prefer official Microsoft libraries where appropriate.

Investigate QEMU, UTM and third-party component licensing before making redistribution decisions.

Do not assume that using a library, bundling a binary and copying source code have identical licensing obligations.

Do not bundle QEMU binaries until the packaging and licensing strategy has been approved.

## Documentation

`PLAN.md` is the authoritative development roadmap after user approval.

Document:

- Architectural decisions.
- Important implementation constraints.
- Unresolved technical risks.
- External dependencies.
- Required system capabilities.
- Testing strategy.
- Milestone acceptance criteria.

Keep documentation concise, accurate and useful.

Do not create large amounts of redundant documentation.

## Commit and Pull Request Guidelines

Use concise English descriptions when suggesting commit messages.

Always end the final response for a completed repository-change task with a concise, single-line English commit message. Do not include milestone identifiers such as M0, M1 or M2 in that message. Suggest the message only; never stage or commit changes.

The user manages Git manually.

Agents must leave all changes unstaged and uncommitted.

At the end of a development task, summarize:

1. Files created or modified.
2. Features implemented.
3. Validation performed.
4. Known limitations.
5. Recommended next step.

Never claim that changes have been committed.

## Git Rules

The user manages Git manually.

Codex MUST NOT perform Git operations that modify repository state or history.

Forbidden commands include:

- `git add`
- `git commit`
- `git push`
- `git pull`
- `git fetch`
- `git merge`
- `git rebase`
- `git reset`
- `git checkout`
- `git switch`
- `git branch`
- `git tag`
- `git stash`
- `git cherry-pick`

Read-only Git commands are allowed when useful:

- `git status`
- `git diff`
- `git log`
- `git show`

Never stage or commit changes automatically.

Never create or switch branches.

Never interact with Git remotes.

Never modify Git configuration or hooks without explicit permission.

Leave all modifications unstaged and uncommitted.

The user will review and manage Git manually.

## Security Rules

Never expose secrets, credentials, tokens or private configuration values.

Never hardcode credentials.

Never delete or overwrite user data without explicit authorization.

Never execute untrusted scripts from the reference repository.

Never modify system-wide Windows settings without explicit approval.

Never disable security features to make virtualization work.

Never run installers, enable Windows features or modify firewall rules without permission.

Prefer the least-privileged approach.

## Agent Behavior

Work carefully, incrementally and transparently.

- Follow the user's current request.
- Respect the current project phase.
- Do not implement unrelated features.
- Do not silently refactor unrelated code.
- Do not invent APIs or technical capabilities.
- Do not hide build failures.
- Do not claim unverified functionality.
- Prefer small, reviewable changes.
- Preserve existing work.
- Ask before destructive or irreversible operations.

The goal is to create a reliable, maintainable Windows virtualization application, not merely a visually convincing prototype.

**The user retains final control over architecture, implementation scope, dependencies and Git operations.**
