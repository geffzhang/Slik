# .NET 10 Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every Slik project and deployment path to .NET 10, refresh direct NuGet dependencies, and release the two libraries as version 3.0.0.

**Architecture:** Retarget all eight projects to `net10.0` only and make `SlikCache` reference the in-solution `SlikSecurity` project. Update dependencies and compatibility issues before migrating existing CI and Docker paths, then update release documentation and verify the generated NuGet package metadata.

**Tech Stack:** .NET 10 SDK/runtime, NuGet, MSTest, GitHub Actions, Docker.

## Global Constraints

- All solution projects target only `net10.0`.
- Update every direct NuGet package reference to the latest stable release available during implementation and pin an explicit stable version.
- Set `Slik.Cache` and `Slik.Security` package versions to `3.0.0`, and their assembly/file versions to `3.0.0.0`.
- `SlikCache` references `SlikSecurity.csproj` as a project reference.
- Preserve existing GitHub Actions workflow triggers and normal-versus-integration workflow separation.
- Use .NET 10 SDK/runtime images and publish the Slik.Cord image with the `10.0` tag.
- Update current .NET support documentation and add a changelog entry; retain historical release notes as historical records.

---

## File Map

| Files | Responsibility |
|---|---|
| `src/SlikCache/SlikCache.csproj`, `src/SlikSecurity/SlikSecurity.csproj`, `src/SlikCord/SlikCord.csproj` | Production target frameworks, dependencies, package metadata, and local library reference |
| `examples/SlikNode/SlikNode.csproj`, `tests/SlikCache.Tests/SlikCache.Tests.csproj`, `tests/SlikCache.IntegrationTests/SlikCache.IntegrationTests.csproj`, `tests/SlikSecurity.Tests/SlikSecurity.Tests.csproj`, `tests/SlikCord.IntegrationTests/SlikCord.IntegrationTests.csproj` | Example and test target frameworks and direct package dependencies |
| `src/SlikCache/SlikCache.cs`, `SlikCacheSnapshotBuilder.cs`, `SlikOptions.cs`, `HostBuilderExtensions.cs`, `SlikRouter.cs`, `SlikMembershipHandler.cs`, `NamedLockFactory.cs` | Port cache replication, state application, snapshots, host initialization, membership changes, and async-lock types to DotNext 6.9 |
| `tests/SlikCache.Tests/*.cs`, `tests/SlikCache.IntegrationTests/*.cs`, test source files under `tests/SlikSecurity.Tests/` and `tests/SlikCord.IntegrationTests/` | Retarget test labels/paths, verify cache behavior, new-format snapshot recovery, cluster replication, and member changes |
| Other existing `.cs` files under `src/`, `examples/`, and `tests/` | Only compatibility fixes demonstrated necessary by build/test failures |
| `.github/workflows/build-and-tests.yml`, `.github/workflows/integration-tests.yml`, `.github/workflows/slik-cord-integration.yml` | .NET SDK setup, target framework selection, and Slik.Cord image publishing/testing |
| `src/SlikCord/Dockerfile`, `examples/SlikNode/Dockerfile` | SDK/runtime base images; SlikNode's project-reference restore inputs |
| `README.md`, `CHANGELOG.md` | Current framework support claims and release notes |

## Task 1: Retarget the solution and connect the local security project

**Files:**
- Modify: `src/SlikCache/SlikCache.csproj`
- Modify: `src/SlikSecurity/SlikSecurity.csproj`
- Modify: `src/SlikCord/SlikCord.csproj`
- Modify: `examples/SlikNode/SlikNode.csproj`
- Modify: `tests/SlikCache.Tests/SlikCache.Tests.csproj`
- Modify: `tests/SlikCache.IntegrationTests/SlikCache.IntegrationTests.csproj`
- Modify: `tests/SlikSecurity.Tests/SlikSecurity.Tests.csproj`
- Modify: `tests/SlikCord.IntegrationTests/SlikCord.IntegrationTests.csproj`

**Interfaces:**
- Consumes: Current project graph and package metadata described in `docs/superpowers/specs/2026-10-07-dotnet10-upgrade-design.md`.
- Produces: Eight `net10.0` projects, two version-3.0.0 packable libraries, and a local `SlikCache` → `SlikSecurity` project reference.

- [ ] **Step 1: Change every project target to `net10.0`**

Replace each `<TargetFrameworks>net5.0;net6.0</TargetFrameworks>` with:

```xml
<TargetFramework>net10.0</TargetFramework>
```

- [ ] **Step 2: Update the two library version properties**

In both `src/SlikCache/SlikCache.csproj` and `src/SlikSecurity/SlikSecurity.csproj`, set:

```xml
<AssemblyVersion>3.0.0.0</AssemblyVersion>
<FileVersion>3.0.0.0</FileVersion>
<Version>3.0.0</Version>
```

- [ ] **Step 3: Reference the in-solution SlikSecurity project**

In `src/SlikCache/SlikCache.csproj`, remove:

```xml
<PackageReference Include="Slik.Security" Version="2.*" />
```

and add:

```xml
<ProjectReference Include="..\SlikSecurity\SlikSecurity.csproj" />
```

- [ ] **Step 4: Verify the project declarations**

Run: `rg -n 'net5\.0|net6\.0' src tests examples --glob '*.csproj'`

Expected: no matches (ripgrep exit code 1).

Run: `dotnet sln Slik.sln list`

Expected: all eight solution projects are listed.

- [ ] **Step 5: Commit the target-framework boundary**

```powershell
git add -- src/SlikCache/SlikCache.csproj src/SlikSecurity/SlikSecurity.csproj src/SlikCord/SlikCord.csproj examples/SlikNode/SlikNode.csproj tests/SlikCache.Tests/SlikCache.Tests.csproj tests/SlikCache.IntegrationTests/SlikCache.IntegrationTests.csproj tests/SlikSecurity.Tests/SlikSecurity.Tests.csproj tests/SlikCord.IntegrationTests/SlikCord.IntegrationTests.csproj
git commit -m "build: retarget solution to .NET 10" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 2: Upgrade direct NuGet dependencies to latest stable versions

**Files:**
- Modify: The eight `.csproj` files listed in Task 1.

**Interfaces:**
- Consumes: The `net10.0` project declarations and local project reference from Task 1.
- Produces: Explicit latest-stable versions for every remaining direct `PackageReference`.

- [ ] **Step 1: Resolve and pin all direct package references**

First run `dotnet restore Slik.sln` and `dotnet list Slik.sln package`; record the `Resolved` stable version for each direct package. On 2026-10-07, the latest stable versions resolved for this solution are the versions below. Run the PowerShell from the repository root. Pass the resolved version explicitly: omitting `--version` writes a floating `*` reference. `--no-restore` avoids restoring an incomplete dependency graph after each package. The Slik.Security package reference is already replaced by the project reference in Task 1.

```powershell
$latestVersions = @{
    "DotNext.AspNetCore.Cluster" = "6.9.0"
    "Grpc.Net.Client" = "2.84.0"
    "Microsoft.Extensions.Caching.Abstractions" = "10.0.12"
    "Microsoft.Extensions.Configuration.Abstractions" = "10.0.12"
    "Microsoft.Extensions.DependencyInjection.Abstractions" = "10.0.12"
    "protobuf-net.Grpc.AspNetCore" = "1.3.14"
    "Microsoft.AspNetCore.Server.Kestrel.Core" = "2.3.13"
    "Microsoft.Extensions.Logging.Abstractions" = "10.0.12"
    "CommandLineParser" = "2.9.1"
    "Microsoft.VisualStudio.Azure.Containers.Tools.Targets" = "1.23.0"
    "Serilog.AspNetCore" = "10.0.0"
    "Serilog.Enrichers.Thread" = "4.0.0"
    "Google.Protobuf" = "3.36.2"
    "Grpc.AspNetCore" = "2.84.0"
    "Grpc.Core" = "2.46.6"
    "Grpc.Tools" = "2.84.0"
    "SharpCompress" = "1.0.0"
    "Microsoft.NET.Test.Sdk" = "18.10.1"
    "Moq" = "4.21.0"
    "MSTest.TestAdapter" = "4.4.1"
    "MSTest.TestFramework" = "4.4.1"
    "coverlet.collector" = "10.1.0"
    "Microsoft.AspNetCore.Mvc.Testing" = "10.0.12"
    "Microsoft.Extensions.Configuration" = "10.0.12"
    "Microsoft.Extensions.Logging" = "10.0.12"
    "Microsoft.Extensions.Logging.Console" = "10.0.12"
    "protobuf-net.Grpc" = "1.3.14"
    "TestEnvironment.Docker" = "2.1.7"
}

$projects = @(
    "src\SlikCache\SlikCache.csproj",
    "src\SlikSecurity\SlikSecurity.csproj",
    "src\SlikCord\SlikCord.csproj",
    "examples\SlikNode\SlikNode.csproj",
    "tests\SlikCache.Tests\SlikCache.Tests.csproj",
    "tests\SlikCache.IntegrationTests\SlikCache.IntegrationTests.csproj",
    "tests\SlikSecurity.Tests\SlikSecurity.Tests.csproj",
    "tests\SlikCord.IntegrationTests\SlikCord.IntegrationTests.csproj"
)

foreach ($project in $projects) {
    [xml]$projectXml = Get-Content $project
    $packageIds = $projectXml.SelectNodes("//PackageReference") |
        ForEach-Object { $_.GetAttribute("Include") } |
        Sort-Object -Unique

    foreach ($packageId in $packageIds) {
        if (-not $latestVersions.ContainsKey($packageId)) {
            throw "No resolved version recorded for $packageId"
        }
        dotnet add $project package $packageId --version $latestVersions[$packageId] --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update $packageId in $project"
        }
    }
}
```

- [ ] **Step 2: Restore the solution**

Run: `dotnet restore Slik.sln`

Expected: restore completes for all eight projects with no unsupported-target (`NU1202`) errors. If a package has no compatible stable release, stop and report the specific package rather than silently downgrading the target framework or keeping a floating range.

- [ ] **Step 3: Confirm no floating direct package versions remain**

Run: `rg -n 'Version="[^"]*[\*]' src tests examples --glob '*.csproj'`

Expected: no matches.

- [ ] **Step 4: Commit the dependency refresh**

```powershell
git add -- src tests examples
git commit -m "build: update NuGet dependencies" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 3: Port SlikCache to the DotNext 6.9 state-machine API

**Files:**
- Modify: `src/SlikCache/SlikCache.cs`
- Modify: `src/SlikCache/SlikCacheSnapshotBuilder.cs`
- Modify: `src/SlikCache/SlikOptions.cs`
- Modify: `src/SlikCache/NamedLockFactory.cs`
- Modify: `tests/SlikCache.Tests/SlikCache.PersistentState.Tests.cs`
- Modify: `tests/SlikCache.Tests/SlikCache.Helper.cs`
- Modify: `tests/SlikCache.Tests/NamedLockFactoryTests.cs`

**Interfaces:**
- Consumes: `DotNext.AspNetCore.Cluster` 6.9.0 and its `SimpleStateMachine`, `IStateMachine`, `WriteAheadLog`, and `LogEntry` APIs.
- Produces: A `SlikCache` state machine that applies replicated cache records and persists/restores snapshots in the new storage format.

- [x] **Step 1: Add/adjust state-machine behavior tests before changing production code**

Replace `InitializeAsync_ExistingLog_AddsItemsCorrectly` in `tests/SlikCache.Tests/SlikCache.PersistentState.Tests.cs` with tests using a fresh temporary DotNext 6.9 WAL location. Cover this sequence: apply updates for two keys, overwrite one key, remove the other, create a snapshot, dispose the WAL, restore a new state machine from that snapshot, and assert the overwritten value remains while the removed key is absent. Clean only the test's temporary directory in `finally`.

Run: `dotnet test tests\SlikCache.Tests\SlikCache.Tests.csproj -c Release --framework net10.0`

Expected before the implementation: the test project fails to compile against the removed `PersistentState`/`LogEntryProducer` APIs. After the state-machine implementation in Step 2, the same test must fail only if replay, snapshot, or recovery behavior is incorrect.

- [x] **Step 2: Replace the legacy PersistentState/SnapshotBuilder integration**

Make `SlikCache` derive from `DotNext.Net.Cluster.Consensus.Raft.StateMachine.SimpleStateMachine`. Decode non-empty application `LogEntry` payloads into `CacheLogRecord` values in `ApplyAsync`, ignoring control entries with no payload; update/remove/refresh the in-memory cache in log order. Implement snapshot persistence and restoration using `PersistAsync` and `RestoreAsync`, retaining `RecordsPerPartition` as the checkpoint interval. Replace the removed `SlikCacheSnapshotBuilder` implementation rather than keeping a second snapshot path. Configure the WAL under `Cache-v6` and never delete existing directories automatically.

- [x] **Step 3: Adapt async locking to the current DotNext reader/writer lock API**

Change `NamedLockFactory` and `NamedLockFactoryTests` from the removed `AsyncLock.Holder` API to `AsyncReaderWriterLock.EnterReadLockAsync`/`EnterWriteLockAsync`. Return a disposable scope wrapper that releases the corresponding reader or writer lock, preserving same-name exclusion and reader concurrency.

- [x] **Step 4: Run the state-machine and cache API tests**

Run: `dotnet test tests\SlikCache.Tests\SlikCache.Tests.csproj -c Release --framework net10.0`

Verified: all 29 cache unit tests pass in Release on `net10.0`, including cache API, named-lock, log replay, and snapshot recovery tests.

- [ ] **Step 5: Commit the DotNext state-machine port**

```powershell
git add -- src examples tests
git commit -m "refactor: port cache state machine to DotNext 6" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 4: Migrate cluster membership, host restoration, and integration tests

**Files:**
- Modify: `src/SlikCache/HostBuilderExtensions.cs`
- Modify: `src/SlikCache/SlikRouter.cs`
- Modify: `src/SlikCache/SlikMembershipHandler.cs`
- Modify: `tests/SlikCache.IntegrationTests/SlikCache.IntegrationTests.cs`
- Modify: test-category declarations under `tests/`

**Interfaces:**
- Consumes: State-machine/WAL implementation and tests from Task 3.
- Produces: Cluster startup that restores snapshots before WAL resolution, uses current Raft replication/membership APIs, and verifies existing cluster behavior.

- [x] **Step 1: Register the new state machine and restore it before Raft startup**

In `HostBuilderExtensions.UseSlik`, register `SlikCache` with `services.UseStateMachine<SlikCache>(new WriteAheadLog.Options { Location = ... })`. Replace the state-machine service factory so it restores the state machine before returning the instance; this must happen before DI constructs the WAL or any hosted service resolves the Raft cluster. Keep cache WAL/snapshots under `Cache-v6` and persistent membership configuration under `Cluster-v6`; do not migrate, remove, or overwrite old storage directories.

- [x] **Step 2: Replicate cache commands through IRaftCluster**

Change the cache replication callback wired by `SlikRouter` to serialize a `CacheLogRecord` and submit it through `IRaftCluster.ReplicateAsync`. Let the `SlikCache` state machine's `ApplyAsync` update local and follower caches. Preserve leader redirection, write rollback/error reporting, and the existing offline unit-test mode.

- [x] **Step 3: Replace dynamic membership API calls**

Inject `IRaftHttpCluster` in `SlikMembershipHandler`; implement additions with `AddMemberAsync(new Uri(member), token)` and removals with `RemoveMemberAsync(new Uri(member), token)`. Serialize membership updates, select a deterministic bootstrap member, and keep non-bootstrap API nodes from cold-starting competing clusters. Configure Raft HTTP transport for exact HTTP/2 to match Kestrel's HTTP/2-only TLS endpoints.

- [x] **Step 4: Add a new-format restart integration test**

Add `Restart_RestoresCacheValueFromNewFormatStorage` to `SlikCacheIntegrationTests`: start a single node in a fresh test directory, set a value through the gRPC cache API, stop the node, restart it with the same new-format directory, and assert the value is restored. Configure the test to create a snapshot before stopping so it verifies snapshot restoration, not only WAL replay. Delete only this test's temporary directory in `finally`.

- [x] **Step 5: Retarget the integration harness and test labels**

Change `TestProjectPath` from `net6.0` to `net10.0`. Replace stale `.Net 5`/`.Net 6` test categories and `#if NET5_0` branches in test source with a single `.NET 10` category where applicable.

- [x] **Step 6: Run cache integration tests**

Run: `dotnet test tests\SlikCache.IntegrationTests\SlikCache.IntegrationTests.csproj -c Release --framework net10.0`

Verified: all 5 non-skipped cache integration tests pass in Release on `net10.0`, including replication, leader redirection, member add/remove, and new-format restart/snapshot recovery.

- [ ] **Step 7: Commit cluster and integration changes**

```powershell
git add -- src/SlikCache tests
git commit -m "fix: migrate Raft replication and membership APIs" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 5: Migrate CI and Docker build/runtime images

**Files:**
- Modify: `.github/workflows/build-and-tests.yml`
- Modify: `.github/workflows/integration-tests.yml`
- Modify: `.github/workflows/slik-cord-integration.yml`
- Modify: `src/SlikCord/SlikCord.csproj`
- Modify: `src/SlikCord/Dockerfile`
- Modify: `examples/SlikNode/Dockerfile`

**Interfaces:**
- Consumes: Successful .NET 10 build and tests from Task 3.
- Produces: Existing CI workflows and both Docker images build/test only the .NET 10 target.

- [ ] **Step 1: Update .NET setup in all workflows**

Replace the .NET 5 and .NET 6 setup steps with one `actions/setup-dotnet@v5` step using `dotnet-version: 10.0.x`. Preserve each workflow's existing triggers, job boundaries, and test separation.

- [ ] **Step 2: Update the Slik.Cord Docker image**

Use .NET 10 SDK and ASP.NET runtime images in `src/SlikCord/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
```

Remove the `FRAMEWORK` build argument and change the publish command in `src/SlikCord/Dockerfile` to:

```dockerfile
RUN dotnet publish "/sln/src/SlikCord/SlikCord.csproj" -c Release -o /app/publish --framework net10.0
```

Remove the corresponding `DockerfileBuildArguments` property from `src/SlikCord/SlikCord.csproj`. In `.github/workflows/slik-cord-integration.yml`, replace the two 5.0/6.0 image build/run/test sections with one build/push/run/test sequence using tag `ghcr.io/insvald/slik-cord:10.0`, no `FRAMEWORK` build argument, and `--framework net10.0`.

- [ ] **Step 3: Update the SlikNode Docker build**

Set both base images in `examples/SlikNode/Dockerfile` to .NET 10. Because `SlikCache` now references `SlikSecurity.csproj`, copy that project file before the Dockerfile's restore command:

```dockerfile
COPY ["src/SlikSecurity/SlikSecurity.csproj", "src/SlikSecurity/"]
```

- [ ] **Step 4: Build both images**

Run: `docker build -f examples\SlikNode\Dockerfile -t slik-node:10.0 .`

Expected: SlikNode image builds successfully, including restore of SlikSecurity through the project reference.

Run: `docker build -f src\SlikCord\Dockerfile -t slik-cord:10.0 .`

Expected: Slik.Cord image builds with .NET 10 SDK/runtime stages.

- [ ] **Step 5: Review the workflow framework and image targets**

Run: `rg -n '5\.0|6\.0|net5\.0|net6\.0|FRAMEWORK=' .github/workflows src examples --glob '*.yml' --glob '*.csproj' --glob 'Dockerfile'`

Expected: no .NET 5/6 SDK, TFM, or Slik.Cord build-argument references remain.

- [ ] **Step 6: Commit CI and container updates**

```powershell
git add -- .github/workflows/build-and-tests.yml .github/workflows/integration-tests.yml .github/workflows/slik-cord-integration.yml src/SlikCord/SlikCord.csproj src/SlikCord/Dockerfile examples/SlikNode/Dockerfile
git commit -m "build: migrate CI and containers to .NET 10" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Task 6: Update support documentation and verify packages

**Files:**
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Verify: `src/SlikCache/SlikCache.csproj`, `src/SlikSecurity/SlikSecurity.csproj`, and generated packages under `artifacts\packages`.

**Interfaces:**
- Consumes: .NET 10 project, CI, and container configuration from Tasks 1–4.
- Produces: Accurate current support/release documentation and verified version-3.0.0 NuGet artifacts.

- [ ] **Step 1: Update current framework claims**

In `README.md`, replace current `.NET 5.0/6.0` support claims with `.NET 10`. Preserve historical release entries in `CHANGELOG.md`; add a new `3.0.0` release entry dated `October 7, 2026` describing .NET 10-only support, the breaking package-target change, and that old DotNext data directories are not automatically migrated or deleted.

- [ ] **Step 2: Run the full release build and test suite**

Run:

```powershell
dotnet restore Slik.sln
dotnet build Slik.sln -c Release --no-restore
dotnet test Slik.sln -c Release --no-restore --framework net10.0
```

Expected: restore, build, and every unit/integration test pass in the available environment.

- [ ] **Step 3: Pack both libraries**

Run:

```powershell
dotnet pack src\SlikSecurity\SlikSecurity.csproj -c Release -o artifacts\packages
dotnet pack src\SlikCache\SlikCache.csproj -c Release -o artifacts\packages
```

Expected: `artifacts\packages\Slik.Security.3.0.0.nupkg` and `artifacts\packages\Slik.Cache.3.0.0.nupkg` are created.

- [ ] **Step 4: Verify package target assets and dependency metadata**

Run:

```powershell
tar -tf artifacts\packages\Slik.Security.3.0.0.nupkg | Select-String '^lib/net10.0/'
tar -tf artifacts\packages\Slik.Cache.3.0.0.nupkg | Select-String '^lib/net10.0/'
tar -xOf artifacts\packages\Slik.Cache.3.0.0.nupkg Slik.Cache.nuspec
```

Expected: each package contains `net10.0` library assets and no `net5.0`/`net6.0` assets; `Slik.Cache.nuspec` declares `Slik.Security` dependency version `3.0.0`.

- [ ] **Step 5: Verify current docs and remove stale build targets**

Run: `rg -n 'Net 5\.0/6\.0|\.NET 5\.0/6\.0|net5\.0|net6\.0' README.md src tests examples .github --glob '*.md' --glob '*.csproj' --glob '*.yml' --glob 'Dockerfile'`

Expected: no current support/build-target claims remain. Historical changelog entries are not included in this scan.

- [ ] **Step 6: Commit documentation and release verification changes**

```powershell
git add -- README.md CHANGELOG.md
git commit -m "docs: document .NET 10 release" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```
