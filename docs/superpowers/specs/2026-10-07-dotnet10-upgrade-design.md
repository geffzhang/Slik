# .NET 10 Upgrade Design

## Goal

Upgrade the Slik solution from .NET 5/.NET 6 to .NET 10 as a single, coordinated change, including application and library projects, tests, dependencies, CI, container images, NuGet metadata, and user-facing documentation.

## Current State

- The solution contains eight projects: `SlikCache`, `SlikSecurity`, `SlikCord`, the `SlikNode` example, and four unit/integration test projects.
- Every project currently targets `net5.0;net6.0`.
- `SlikCache` currently consumes `Slik.Security` as a NuGet package even though the `SlikSecurity` project is in the same solution.
- `SlikCache` directly depends on DotNext 3.x Raft APIs, including `PersistentState`, `SnapshotBuilder`, `IExpandableCluster`, and `AsyncLock.Holder`.
- The two packable libraries, `Slik.Cache` and `Slik.Security`, declare package version `2.0.1` and assembly/file version `2.0.1.0`.
- Build and integration workflows install .NET 5 and .NET 6. Dockerfiles use .NET 6 SDK/runtime images; the Slik.Cord workflow also publishes separate 5.0 and 6.0 images.
- The README describes .NET 5/6 support. NuGet package references use broad floating version ranges.

## Chosen Approach

Perform an atomic, solution-wide transition to `net10.0`. Do not retain .NET 5 or .NET 6 target frameworks. This avoids a temporary multi-target compatibility promise and keeps libraries, applications, tests, CI, and images on one runtime target. Upgrade DotNext to its latest stable release and port SlikCache's Raft integration to that release's state-machine/WAL APIs rather than pinning the old major version.

## Scope and Design

1. **Project targets:** Change all eight project files to target only `net10.0`.
2. **NuGet dependencies:** Update every direct package reference to the latest stable release available during implementation, pinning explicit stable versions rather than retaining floating ranges. Resolve any API or behavior changes required by the upgraded packages. Do not add unrelated refactoring or explicitly pin transitive packages unless a demonstrated restore/build issue requires it.
3. **Library release metadata:** Set both `Slik.Cache` and `Slik.Security` package versions to `3.0.0`, reflecting the breaking removal of older target frameworks. Update their declared assembly and file versions from `2.0.1.0` to `3.0.0.0`.
4. **In-repository library dependency:** Replace `SlikCache`'s `Slik.Security` package reference with a project reference to `src/SlikSecurity/SlikSecurity.csproj`. This ensures the solution builds and tests against the in-repository 3.0.0 source before either NuGet package is published.
5. **DotNext Raft API port:** Replace the old `PersistentState`/`SnapshotBuilder` integration with DotNext's `SimpleStateMachine` and `WriteAheadLog`, restoring the state machine before the WAL or cluster resolves it. Replicate cache commands through the new Raft API and retain cache read/write, leader redirection, rollback-on-failed-write, snapshot, and dynamic member add/remove behavior. Use the current `IRaftHttpCluster` membership API and current async-lock scope type.
6. **Existing persisted data:** Automatic migration/backward compatibility for directories written by the old DotNext API is not required. The application must not delete or overwrite old directories as an upgrade side effect; document that operators must back up and initialize compatible storage for the new format.
7. **CI:** Update each existing workflow to install/use the .NET 10 SDK, build/test the `net10.0` projects, and use a currently supported `setup-dotnet` action version. Preserve current workflow triggers and the separation between normal and manually/integration-triggered test workflows.
8. **Containers:** Update both Dockerfiles to .NET 10 SDK/runtime images. Update the Slik.Cord workflow to publish and exercise the .NET 10 image with a `10.0` tag instead of building new 5.0/6.0 variants. Update SlikNode's Docker build to use .NET 10.
9. **Documentation:** Update README framework claims and add a changelog entry describing the .NET 10-only target, package major-version change, and lack of automatic migration for old DotNext data directories.

The intended consumer-visible behavior change is that the two NuGet packages ship only .NET 10 assets; consumers on older runtimes must stay on the prior package major version or upgrade their applications.

## Validation and Acceptance Criteria

- Restore and build the full solution in Release configuration using .NET 10.
- Run the unit and integration test projects against `net10.0` in their existing workflow contexts; do not skip or silently downgrade failing tests.
- Verify cache command application, replication, leader redirection, dynamic member add/remove, and snapshot persistence/recovery with tests using newly initialized storage.
- Pack both library projects and verify each package is version `3.0.0` and contains only `net10.0` target assets.
- Verify the packed `Slik.Cache` package declares a dependency on `Slik.Security` version `3.0.0`.
- Build both Docker images and verify the Slik.Cord image starts for its integration workflow.
- Confirm project, CI, Docker, and README configuration no longer advertises or builds .NET 5/6 targets.

## Risks and Handling

- Latest stable major releases of existing dependencies may introduce source/API or runtime behavior changes. Make only changes needed to retain intended behavior, and use the affected builds and tests to verify them.
- DotNext's new state-machine/WAL lifecycle requires snapshot restoration before WAL construction. Restore ordering must be explicit and tested so snapshots are not silently skipped.
- The package target-framework and major-version changes are intentionally breaking for older consumers; document this clearly rather than retaining an unsupported compatibility target.
- Old DotNext data directories are outside the compatibility guarantee. Never delete or reset them automatically; state the requirement for backup/new storage in the release notes.
- Docker or integration-test prerequisites may make some checks available only in CI. Keep those checks represented in their existing workflows and report any environment limitation explicitly.
