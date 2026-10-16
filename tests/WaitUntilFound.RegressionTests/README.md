# Wait Until Found Regression Tests

This dependency-free console harness exercises the real image-wait loop and macro
executor with fake matching results. It does not start the app, capture the screen,
or send mouse input. Every macro uses an unsupported operation as an additional
guard against accidental input if a regression occurs.

Run from the repository root on Windows using the local development SDK:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\dev.ps1 run --project tests/WaitUntilFound.RegressionTests/WaitUntilFound.RegressionTests.csproj
```

If the .NET 10 SDK is already on `PATH`, `dotnet run --project
tests/WaitUntilFound.RegressionTests/WaitUntilFound.RegressionTests.csproj` is equivalent.

The harness exits with code 0 only when all checks pass. The run takes at least
31 seconds to verify that a checked action survives the old 30-second deadline.
It also checks retry-until-found, cancellation, finite timeouts, unrecoverable
errors, single-attempt behavior when unchecked, and stopping during a successful
match. Individual completion waits have a five-second deadline.

The normal application build and publish exclude this test directory. No new
NuGet packages or test framework are required beyond the application's existing
dependencies.
