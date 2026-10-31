# Versioning

Every CodeLogic package — the framework and all official `CodeLogic.*` libraries —
shares one version line per major version: **`5.0.<patch>`** on .NET 11 (branch `main`)
and **`4.9.<patch>`** on .NET 10 (branch `support/4.x`). See [Releasing](#releasing).

Only the patch number moves. It is never hand-edited.

---

## The scheme

```text
4.9.142+a1b2c3d
│ │ │   └── git short sha  (build metadata, provenance only)
│ │ └────── patch          (CI run number, automatic)
│ └──────── minor          (version.txt, hand-controlled)
└────────── major          (version.txt, hand-controlled)
```

| Component | Source | Changes when |
|-----------|--------|--------------|
| `major.minor` | `version.txt` at the repo root | You edit the file. Never automatic. |
| `patch` | GitHub Actions run number | Every CI run. Monotonic. |
| `+sha` | Git commit | Every commit. Stripped by nuget.org. |

`version.txt` contains exactly one line — `4.9` — in both the `CodeLogic` and
`CodeLogic.Libs` repositories. Keeping the two files in step is what keeps the
framework and the libraries on a single, legible version line.

---

## Why the patch is the run number

The run number is monotonic and never collides, so no release can accidentally
reuse or move backwards over a published version. It needs no commit, no
write-back to the repository, and no coordination between the two repos.

The trade-off is that numbering has **gaps**. A failed run, or a run of another
workflow, still consumes a number. `4.9.141` may be followed by `4.9.145`. This
is expected and harmless — NuGet orders by value, not by adjacency.

## Why the sha is build metadata

Everything after `+` is SemVer *build metadata*. NuGet.org strips it from the
package version and ignores it entirely for ordering, so `4.9.142+a1b2c3d`
publishes as the package `4.9.142`.

It survives where it is useful: in the assembly's `InformationalVersion`, so a
deployed binary can always be traced back to the exact commit that produced it.

---

## Assembly identity

`AssemblyVersion` and `FileVersion` are **not** the package version. They are
pinned to `Major.Minor.0.0` — for the 4.9 line, always `4.9.0.0`.

Win32 version fields are four `UInt16`s and cannot carry a `-prerelease` or
`+metadata` suffix, so the full composed string could not go there even if it
were desirable. Pinning has a second, larger benefit: **CLR assembly binding
stays fixed across the whole minor line**, so every `4.9.x` patch loads
interchangeably. Consumers never need a binding redirect to take a patch.

Moving `version.txt` to `4.10` moves the binding identity to `4.10.0.0`, which is
a binding-breaking change for anything compiled against `4.9.0.0`. That is the
signal a minor bump is meant to carry.

---

## Releasing

Each major version is a **release line** with its own branches. Both repositories (CodeLogic and
CodeLogic.Libs) use the same layout:

| Line | Target | Integration branch | Preview channel | Stable channel |
|------|--------|--------------------|-----------------|----------------|
| 5.x | .NET 11 | `main` | `prerelease/5.x` | `release/5.x` |
| 4.x | .NET 10 | `support/4.x` | `prerelease/4.x` | `release/4.x` |

| Branch | Who writes to it | What happens |
|--------|------------------|--------------|
| `main`, `support/4.x` | Pull requests only. Required checks (the full suite, including the database jobs in CodeLogic.Libs) must pass. | Nothing is published. |
| `prerelease/<N>.x` | The **Promote** workflow only (protected). | Publishes `<major.minor>.<run>-preview+<sha>`, tagged as a GitHub pre-release. |
| `release/<N>.x` | The **Promote** workflow only (protected). | Publishes `<major.minor>.<run>+<sha>`, tagged as a full GitHub release. |

Nothing is written back to the repository, and nothing promotes automatically.

### Shipping a change

1. Branch from the line's integration branch (`main` for 5.x, `support/4.x` for 4.x) and open a pull
   request back into it. CI runs the full suite; merge when it is green.
2. Run **Promote** from the Actions tab. Pick the `source` (`main` or `support/4.x`) and the `channel`
   (`prerelease` or `release`). It merges the source into `<channel>/<major>.x`, reading the major
   from the source's `version.txt`, and refuses to merge across lines.
3. The release workflow on that branch re-runs the full suite, packs, pre-flights every package and
   publishes. In CodeLogic.Libs only libraries changed since that line's last release tag
   (`libs-v<major.minor>-<run>`) are packed.

A fix that applies to both lines is made on one integration branch and cherry-picked in a second
pull request into the other.

CodeLogic.Libs depends on the framework line of the same major, so publish a CodeLogic change
before the library change that needs it.

A local or developer build leaves the patch at `0`, producing a stable `<major.minor>.0`.
That value is never published; it only keeps local builds reproducible.

### Starting the next major

When `main` moves to 6.0: create `support/5.x` from `main` (it becomes the 5.x integration branch),
add it to the `source` choices in `.github/workflows/promote.yml`, then raise `version.txt` on `main`.
The first Promote of `main` creates `prerelease/6.x` / `release/6.x`. No other workflow change is needed.

### Publishing

The release job publishes to nuget.org with **Trusted Publishing**: the `NuGet/login`
action exchanges the workflow's GitHub OIDC token for a short-lived API key, so no
long-lived key is stored in the repository. The job runs in the `production`
environment and logs in as `vars.NUGET_USER`; both must match the Trusted Publishing
policy on nuget.org (owner `zyntal.com`, workflow `release.yml`).

The NuGet "Release Notes" of each package come from its `CHANGELOG.md`, capped below
nuget.org's 35 000 character limit; the full changelog ships inside the package.

### Moving to the next minor

Edit `version.txt` in **both** repositories, in the same change:

```text
4.9   →   4.10
```

Then run Promote as usual. The next run publishes `4.9.<run>`. Nothing
else needs touching — no csproj, no workflow, no documentation constant.

Because this changes assembly binding identity, treat it as a deliberate,
announced change rather than routine maintenance.
