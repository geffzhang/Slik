# Slik.Cord Content Test GC Roots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make committed content test fixtures survive containerd GC until each test explicitly deletes them.

**Architecture:** Keep production containerd and Slik.Cord behavior unchanged. Mark test fixture commits with containerd's documented `containerd.io/gc.root` label, and retain that label when the update-label test replaces content labels.

**Tech Stack:** .NET 10, C#, MSTest, gRPC, containerd 1.5.1.

## Global Constraints

- Apply the GC-root label only to test fixture content.
- Keep production GC enabled and preserve explicit fixture deletion.
- Leave `examples/SlikNode/Dockerfile`, `src/SlikCord/Dockerfile`, and Slik.Cord runtime code unchanged.
- Do not stage or commit unrelated .NET 10 migration changes already present in the worktree.

---

### Task 1: Root committed content fixtures during Slik.Cord tests

**Files:**
- Modify: `tests/SlikCord.IntegrationTests/ContentServiceTests.cs`

**Interfaces:**
- Consumes: Existing `GetTestObject()`, `UsingTestObject(...)`, and `WriteContentRequest.Labels`.
- Produces: Committed fixture content carrying the `containerd.io/gc.root` label until explicit deletion.

- [x] **Step 1: Add a failing assertion for the missing GC-root label**

Add this constant and test to `ContentServiceTests`:

```csharp
private const string GcRootLabel = "containerd.io/gc.root";

[TestMethod]
public async Task CommittedTestContent_IsMarkedAsGcRoot()
{
    await UsingTestObject(async digest =>
    {
        var response = await _client.InfoAsync(new InfoRequest { Digest = digest }, Headers);
        Assert.IsTrue(response.Info.Labels.ContainsKey(GcRootLabel));
    });
}
```

Also add this assertion to `Update_ExistingObject_AddsLabels`, after the existing test-label assertion:

```csharp
Assert.IsTrue(response.Info.Labels.ContainsKey(GcRootLabel));
```

- [x] **Step 2: Run the new test and confirm the expected failure**

Run:

```powershell
dotnet test tests\SlikCord.IntegrationTests\SlikCord.IntegrationTests.csproj -c Release --framework net10.0 --no-restore --filter "FullyQualifiedName~CommittedTestContent_IsMarkedAsGcRoot"
```

Expected: the test fails at `ContainsKey(GcRootLabel)` because the current commit request has no GC-root label.

- [x] **Step 3: Mark each committed fixture as a GC root**

In `GetTestObject()`, replace the inline commit request with:

```csharp
var commitRequest = new WriteContentRequest
{
    Action = WriteAction.Commit,
    Data = GetRandomBytes(),
    Ref = objectRef
};
commitRequest.Labels.Add(GcRootLabel, DateTime.UtcNow.ToString("O"));
await streamingCall.RequestStream.WriteAsync(commitRequest);
```

In `Update_ExistingObject_AddsLabels`, add the GC-root label to the replacement label map before adding the test label:

```csharp
request.Info.Labels.Add(GcRootLabel, DateTime.UtcNow.ToString("O"));
request.Info.Labels.Add(testKey, testValue);
```

- [x] **Step 4: Repeat the focused content test suite**

Run the content service tests three times:

```powershell
for ($attempt = 1; $attempt -le 3; $attempt++) {
    dotnet test tests\SlikCord.IntegrationTests\SlikCord.IntegrationTests.csproj -c Release --framework net10.0 --no-restore --filter "FullyQualifiedName~ContentServiceTests"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

Expected: all content tests pass on each run; only tests already marked skipped remain skipped.

- [x] **Step 5: Run the complete Slik.Cord integration suite**

Run:

```powershell
dotnet test tests\SlikCord.IntegrationTests\SlikCord.IntegrationTests.csproj -c Release --framework net10.0 --no-restore
```

Expected: all tests pass except those already marked skipped, with no content digest `NotFound` failures.

- [x] **Step 6: Check whitespace and confirm scope**

Run:

```powershell
git diff --check
git diff -- tests/SlikCord.IntegrationTests/ContentServiceTests.cs examples/SlikNode/Dockerfile src/SlikCord/Dockerfile
```

Expected: the only implementation change is in `ContentServiceTests.cs`; neither Dockerfile changes.
