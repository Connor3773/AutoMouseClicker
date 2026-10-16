using System.Diagnostics;
using System.Windows;
using Ming_AutoClicker.Models;
using Ming_AutoClicker.Services;

internal static partial class Program
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [STAThread]
    private static int Main()
    {
        // Initialize WPF resource loading without the real App, windows, or a dispatcher loop.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            _ = LocalizationService.Current.GetString("ExecutorCancelled");
            var editorFailures = RunEditorTests(application);
            var executionResult = Task.Run(RunAsync).GetAwaiter().GetResult();
            return editorFailures == 0 ? executionResult : 1;
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static async Task<int> RunAsync()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Infinite wait retries until a match is found", RetriesUntilFoundAsync),
            ("Infinite wait exits when cancelled", InfiniteWaitCancelsAsync),
            ("Finite waits still time out", FiniteWaitTimesOutAsync),
            ("Invalid templates fail immediately", () => FailsImmediatelyAsync(MatchFailureReason.InvalidTemplate)),
            ("Capture errors fail immediately", () => FailsImmediatelyAsync(MatchFailureReason.CaptureFailed)),
            ("Matching errors fail immediately", () => FailsImmediatelyAsync(MatchFailureReason.MatchingError)),
            ("Unchecked actions attempt one match", UncheckedActionMatchesOnceAsync),
            ("Infinite waiting overrides skip timeout beyond 31 seconds", CheckedActionWaitsPastThirtySecondsAsync),
            ("Stopping during a successful match prevents action execution", StopDuringSuccessfulMatchAsync),
            .. SkipTests
        ];

        var failures = 0;
        foreach (var test in tests)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} regression tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RetriesUntilFoundAsync()
    {
        using var capture = new ScreenCaptureService();
        var expected = new MatchResult { Found = true };
        using var matcher = new FakeMatcher(capture, call => call == 3 ? expected : MatchResult.NotFound);
        using var cancellation = new CancellationTokenSource(Deadline);

        var result = await matcher.WaitForImageAsync("unused.png", timeoutMs: Timeout.Infinite,
            intervalMs: 5, cancellationToken: cancellation.Token).WaitAsync(Deadline);

        Require(ReferenceEquals(result, expected) && result.Found, "The successful result was not returned.");
        Require(matcher.Calls == 3, "Matching must retry misses and stop at the first match.");
    }

    private static async Task InfiniteWaitCancelsAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var cancellation = new CancellationTokenSource(Deadline);
        var pending = matcher.WaitForImageAsync("unused.png", timeoutMs: Timeout.Infinite,
            intervalMs: 10, cancellationToken: cancellation.Token);
        try
        {
            await Task.Delay(60);
            Require(!pending.IsCompleted, "An infinite wait must not finish while the image is absent.");
            cancellation.Cancel();
            var result = await pending.WaitAsync(Deadline);
            Require(!result.Found && result.FailureReason == MatchFailureReason.Cancelled,
                "Cancellation must return a cancelled result.");
            Require(matcher.Calls >= 2, "The wait must perform repeated matching attempts.");
        }
        finally
        {
            cancellation.Cancel();
            await pending.WaitAsync(Deadline);
        }
    }

    private static async Task FiniteWaitTimesOutAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var cancellation = new CancellationTokenSource(Deadline);

        var result = await matcher.WaitForImageAsync("unused.png", timeoutMs: 40,
            intervalMs: 5, cancellationToken: cancellation.Token).WaitAsync(Deadline);

        Require(!result.Found && result.FailureReason == MatchFailureReason.TimedOut,
            "A finite timeout must still return TimedOut.");
        Require(result.ElapsedMilliseconds >= 40 && matcher.Calls > 0,
            "The finite wait must attempt matching until its deadline.");
    }

    private static async Task FailsImmediatelyAsync(MatchFailureReason reason)
    {
        using var capture = new ScreenCaptureService();
        var expected = new MatchResult { FailureReason = reason };
        using var matcher = new FakeMatcher(capture, _ => expected);
        using var cancellation = new CancellationTokenSource(Deadline);

        var result = await matcher.WaitForImageAsync("unused.png", timeoutMs: Timeout.Infinite,
            intervalMs: 5, cancellationToken: cancellation.Token).WaitAsync(Deadline);

        Require(ReferenceEquals(result, expected), "The original error must be returned.");
        Require(matcher.Calls == 1, $"{reason} must not be retried.");
    }

    private static async Task UncheckedActionMatchesOnceAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture);
        var completion = ObserveCompletion(executor);
        try
        {
            var profile = CreateProfile(waitUntilFound: false);
            ((FindImageAction)profile.Actions[0]).SkipTimeoutSeconds = 300;
            Require(executor.Start(profile), "The macro did not start.");
            var result = await completion.WaitAsync(Deadline);
            Require(!result.Success && executor.State == MacroExecutionState.Stopped,
                "An unchecked action must still stop on a missing image.");
            Require(matcher.Calls == 1, "An unchecked action must not retry.");
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }

    private static async Task CheckedActionWaitsPastThirtySecondsAsync()
    {
        using var capture = new ScreenCaptureService();
        var firstMatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var matcher = new FakeMatcher(capture, _ =>
        {
            firstMatch.TrySetResult();
            return MatchResult.NotFound;
        });
        using var executor = new MacroExecutor(matcher, capture) { FindImageIntervalMs = 20 };
        var completion = ObserveCompletion(executor);
        try
        {
            var profile = CreateProfile(waitUntilFound: true);
            var action = (FindImageAction)profile.Actions[0];
            action.SkipIfNotFound = true;
            action.SkipTimeoutSeconds = 1;
            Require(executor.Start(profile), "The macro did not start.");
            await firstMatch.Task.WaitAsync(Deadline);
            await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(31)));
            Require(!completion.IsCompleted && executor.IsRunning,
                "A checked action stopped instead of waiting beyond the old 30-second limit.");
            Require(matcher.Calls >= 2, "The checked action must keep matching.");

            executor.Stop();
            RequireCancelled(await completion.WaitAsync(Deadline), executor);
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }

    private static async Task StopDuringSuccessfulMatchAsync()
    {
        using var capture = new ScreenCaptureService();
        Action? stop = null;
        using var matcher = new FakeMatcher(capture, _ =>
        {
            stop!();
            return new MatchResult { Found = true };
        });
        using var executor = new MacroExecutor(matcher, capture);
        stop = executor.Stop;
        var actionEvents = 0;
        executor.ActionExecuted += (_, _) => Interlocked.Increment(ref actionEvents);
        var completion = ObserveCompletion(executor);
        try
        {
            Require(executor.Start(CreateProfile(waitUntilFound: true)), "The macro did not start.");
            RequireCancelled(await completion.WaitAsync(Deadline), executor);
            Require(matcher.Calls == 1 && Volatile.Read(ref actionEvents) == 0,
                "Cancellation during matching must prevent executing the matched action.");
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }

    private static MacroProfile CreateProfile(bool waitUntilFound)
    {
        var profile = new MacroProfile();
        profile.Actions.Add(new FindImageAction
        {
            ImagePath = "unused.png",
            WaitUntilFound = waitUntilFound,
            // This intentionally unsupported operation cannot send mouse input, even if a test fails.
            Operation = "test-no-input"
        });
        return profile;
    }

    private static Task<MacroExecutionEventArgs> ObserveCompletion(MacroExecutor executor)
    {
        var completion = new TaskCompletionSource<MacroExecutionEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        executor.ExecutionCompleted += (_, result) => completion.TrySetResult(result);
        return completion.Task;
    }

    private static void RequireCancelled(MacroExecutionEventArgs result, MacroExecutor executor)
    {
        Require(!result.Success && executor.State == MacroExecutionState.Stopped,
            "Manual stop must leave the executor stopped.");
        Require(result.Message == LocalizationService.Current.GetString("ExecutorCancelled"),
            "Manual stop must complete through the cancellation path, not action failure.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeMatcher(ScreenCaptureService capture, Func<int, MatchResult> find)
        : ImageMatchService(capture)
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public override MatchResult FindImage(string templatePath, double threshold = DefaultThreshold,
            bool adaptiveScale = false) => find(Interlocked.Increment(ref _calls));
    }
}
