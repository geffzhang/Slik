# Slik.Cord Content Test GC Roots

## Problem

`ContentServiceTests` uploads committed blobs into containerd and then performs
separate `Info`, `Read`, or `Update` calls. These fixture blobs are not held by
a lease or referenced by another resource. Containerd 1.5.1 therefore treats
them as eligible for garbage collection; a GC scheduled by another content
operation can remove a blob before its test finishes, yielding `NotFound`.

The SlikNode Dockerfile is not involved: these tests run the Slik.Cord
integration-test container, which bundles containerd.

## Design

Keep production GC behavior unchanged. In `ContentServiceTests`, mark each
committed fixture blob as a containerd GC root by setting the documented,
non-empty `containerd.io/gc.root` label on its commit request. Preserve this
label when the update-label test replaces content labels. Existing fixture
cleanup continues to explicitly delete each blob, allowing containerd GC to
reclaim it after the test.

No changes are planned for Slik.Cord runtime code, containerd configuration,
or either Dockerfile.

## Behavior and Error Handling

The root label is test-only and only protects content for the lifetime of the
fixture. Info, Read, Update, and Delete remain real containerd operations.
Unexpected RPC errors, including `NotFound` before explicit deletion, continue
to fail the test rather than being swallowed.

## Validation

1. Run the ContentService tests repeatedly to exercise GC scheduling.
2. Run the complete Slik.Cord integration-test project.
3. Confirm the SlikNode Dockerfile, Slik.Cord runtime, and GC configuration
   remain unchanged by this fix.
