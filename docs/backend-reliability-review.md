# Backend reliability review

Baseline: `v2.2.0-develop.404`, commit `c55fdc66a410ad94f2d09261ed2fbb5e00dbc8c0`.

This development branch starts from the installed version. Changes should be tested against synthetic fixtures before staging with a disposable database copy. Production deployment is a separate step.

## First patch: avoid redundant scans for an indexed collision

`ImportApprovedEpisodes.Import` currently queues a full series scan whenever a transfer throws `DestinationAlreadyExistsException`. A conflicting destination may already be represented in the library database. Repeating the scan cannot decide which episode should own it, and repeated completed-download processing can enqueue the same work indefinitely.

The patch carries the destination path as structured exception data and checks for an existing library file at that path. An indexed collision keeps its failed import result and warning, but does not request another full scan. An unknown or unindexed destination retains the existing recovery scan.

This does not mark the download imported, ignore it, resolve conflicting ownership, delete media, or change the database schema. Automatic import retries and warnings remain; the avoided work is the redundant full-library scan. A later independent scan can still run normally.

Validation should cover repeated indexed collisions, unindexed destinations, exceptions without destination-path information, paths outside the series folder, database lookup failures, and successful unrelated imports. A lookup failure is treated as unknown ownership so it retains the recovery scan and does not abort the batch. Existing disk-transfer tests must still verify that an occupied destination is not overwritten.

## Additional improvements to consider

### Report external-service failures separately from empty results

`HttpIndexerBase` catches authorization errors, HTTP failures, and timeouts, records the provider failure, and returns empty or partial results. `FetchAndParseRssService.FetchIndexer` and `ReleaseSearchService.DispatchIndexer` also turn remaining exceptions into empty lists. The aggregate result therefore cannot distinguish a successful empty response from an unavailable provider. Logging and health checks contain some of the error information, but it is missing from the result itself. A command can finish successfully even when its provider requests failed.

A separate diagnostic result should retain provider ID, attempted/skipped state, HTTP or protocol error category, backoff deadline, elapsed time, and result count. Preserve partial successes and redact credentials. Tests should use mocked providers for successful-empty, authorization failure, timeout, backoff, and mixed outcomes. This is an observability change; it need not alter queries, matching policy, or download decisions.

`HealthCheckService.Results` returns cached warnings. Provider changes normally refresh them after a short debounce, but expiration of a provider's backoff does not itself refresh the cache. Include a last-checked timestamp and sanitized failure category. Absence of a cached warning is not proof that a connection check succeeded.

### Validate Stash GraphQL responses

`StashProxy.CheckForError` is currently a TODO. An HTTP 200 response containing GraphQL errors can pass as success. Validate the response envelope and expected data before reporting success. Keep error messages useful while excluding credentials and unrelated response data.

The notification also discards the returned scan job ID. Retaining that ID would let diagnostics distinguish a request being accepted, queued, running, completed, or failed. A successful request must not be presented as a completed scan or completed identification.

### Validate path-map containment

`StashService.Update` subtracts `MapFrom` from the series location without first validating containment. Add a component-aware root check so a sibling such as `/library-old` cannot be mapped as though it were inside `/library`. Test exact roots, nested folders, sibling prefixes, separators, and platform case rules. The notification should report a configuration error before sending an invalid scan path.

## Development and review

- Work in `codex/backend-reliability`; retain upstream `v2-develop` as the baseline.
- Build with the .NET 10 SDK. Dependencies are declared by the existing project files.
- Use the existing Common and Core NUnit test projects; no production service is required for the targeted unit tests.
- Keep application backups, library metadata, credentials, and local SDK/cache files outside this repository.
- Before deployment, use a separate configuration directory, disable external integrations, and validate against a disposable backup. Do not point a development process at the running application's SQLite files.
- Retain the current production image and a native application backup for rollback. This branch contains no database migration.

## Initial validation

Validated with .NET SDK 10.0.401 on Windows x64, with the existing analyzers enabled:

- `ImportApprovedEpisodesFixture`: 26 passed, one platform-specific test skipped.
- `DiskTransferServiceFixture`: 45 passed, two platform-specific tests skipped.
- Independent code review and `git diff --check` passed.

These are focused unit tests using synthetic data, not a complete test-suite run or Linux container staging. No live application or database was used for testing. The shared test logging initialization has a first-use ordering issue; the import fixture explicitly initializes logging after inherited setup so warning assertions remain active on the first test.

When running a project directly, pass `SolutionDir` so the repository's StyleCop configuration is loaded. From the repository root in PowerShell:

```powershell
$solutionDir = (Resolve-Path ./src).Path + [IO.Path]::DirectorySeparatorChar
dotnet test src/NzbDrone.Core.Test/Whisparr.Core.Test.csproj -p:RuntimeIdentifiers=win-x64 "-p:SolutionDir=$solutionDir" --filter 'FullyQualifiedName~ImportApprovedEpisodesFixture'
dotnet test src/NzbDrone.Common.Test/Whisparr.Common.Test.csproj -p:RuntimeIdentifiers=win-x64 "-p:SolutionDir=$solutionDir" --filter 'FullyQualifiedName~DiskTransferServiceFixture'
```
