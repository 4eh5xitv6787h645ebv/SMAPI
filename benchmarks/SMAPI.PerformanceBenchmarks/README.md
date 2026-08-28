# Deterministic performance regression suite

This fixture-free suite calls stable SMAPI hot paths with synthetic inputs. Correctness digests and
managed allocation budgets are required gates. Wall-clock results are recorded for comparison but
are informational because shared CI runners don't provide stable timing guarantees.

The committed baseline applies only when the target framework, exact runtime patch, and portable
runtime identifier all match. A mismatched runtime fails before measurements begin instead of
silently applying unrelated allocation numbers.

```sh
dotnet run --project benchmarks/SMAPI.PerformanceBenchmarks -c Release \
  -p:GamePath=/path/to/game-reference-assemblies \
  -p:CopyToGameFolder=false -- \
  --baseline benchmarks/SMAPI.PerformanceBenchmarks/baselines/linux-x64-net6.json \
  --output artifacts/performance-regression \
  --commit "$(git rev-parse HEAD)"
```

Run `--self-test` to verify the gate comparison and artifact writers, or `--list` to list registered
scenarios. Use repeatable `--scenario ID` arguments for a focused run.

Each scenario prepares fixtures in `Setup`, performs its full fixed batch in `Execute(operations)`,
returns a deterministic unsigned 64-bit digest, and releases resources in `Cleanup`. The runner
measures only `Execute`. It takes five independent allocation/timing samples and requires every
sample to match the digest and remain within the absolute per-operation allocation budget.

## Blocking coverage

The console scenarios provide the committed machine-readable baseline and comparison artifacts. The
focused `PerformanceRegression` NUnit category is a second required qualification layer for game-bound
paths that must execute through the test host in an isolated game environment.

| Area | Blocking coverage |
| --- | --- |
| map/TMX conversion | synthetic dense-map correctness and allocation comparison in NUnit |
| canonical paths | exact-zero console gate and NUnit gate; allocating normalization has an absolute console ceiling |
| JSON streaming | console ceiling for a one-megabyte file and NUnit large-versus-small allocation delta |
| parsed asset names | exact-zero NUnit cache-hit and localized base-name gates |
| cached reflection | exact-zero console and NUnit field/property/method wrapper gates |
| event dispatch | warmed stateful multi-handler NUnit gate |
| inventory and chest idle tracking | allocation-free NUnit gates for a 36-slot inventory and 32 representative chests |
| content invalidation | predicate-cache, operation-cache, and content-cache NUnit gates plus exact-zero console enumeration |
| runner infrastructure | digest, allocation, runtime, timing-non-gate, and deterministic writer self-tests |

Run the focused test layer with the same public reference assemblies:

```sh
dotnet test src/SMAPI.Tests/SMAPI.Tests.csproj \
  -c Release \
  --filter "TestCategory=PerformanceRegression" \
  -p:GamePath=/path/to/game-reference-assemblies \
  -p:CopyToGameFolder=false \
  -p:AllowMissingPrunePackageData=true
```

## Baselines and artifacts

`baseline.schema.json` defines the versioned baseline contract. The committed
`baselines/linux-x64-net6.json` baseline pins .NET 6.0.36 and Linux x64, and records each scenario's
operation count, warm-up count, expected digest, absolute allocation ceiling, and informational
timing reference. CI never updates or accepts a baseline automatically.

Every console run writes:

- `results.json`, containing runtime/environment metadata, each raw sample, aggregate comparisons,
  gate decisions, and failure reasons;
- `comparison.md`, a readable table which explicitly labels timing as informational.

The workflow appends the Markdown report to the job summary and uploads both result files even when a
required gate fails. It uses read-only repository permissions and downloads only the public reference
assemblies pinned in the workflow. Those assemblies are intentionally compile-only, so CI builds the
game-bound NUnit gates but executes only the standalone scenarios and infrastructure checks. The full
focused NUnit category is run in the isolated release-qualification environment. CI does not contain,
fetch, or inspect any private benchmark fixture, modpack, or save.

## Intentional regression and stability validation

Before making the workflow required, verify both gates with temporary changes which are never
committed:

1. Bypass mixed-separator detection in `PathUtilities.NormalizePath`; `path.normalize` must fail its
   deterministic digest gate.
2. Restore that exact change and verify the file is clean.
3. Make the canonical fast path return a copied string; `path.canonical` must retain its digest but
   fail its zero-allocation gate.
4. Restore that exact change and verify the file is clean.
5. Run the clean suite repeatedly. Digests and allocation samples must remain stable; timing may vary.

The built-in `--self-test` independently proves that the comparer rejects wrong digests, rejects an
allocation overage, rejects runtime mismatches, and does not fail on timing changes. Any intentional
probe output belongs in pull-request evidence, not in committed baselines or artifacts.
