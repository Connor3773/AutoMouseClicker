using System.Diagnostics;
using System.Text.Json;
using Ming_AutoClicker.Models;
using Ming_AutoClicker.Services;

internal static partial class Program
{
    private static (string Name, Func<Task> Run)[] SkipTests =>
    [
        ("Zero-second skip attempts once and continues", () => SkipContinuesAsync(0)),
        ("Timed skip retries before continuing", () => SkipContinuesAsync(1)),
        ("Immediate skips preserve loop count and interval", () => SkipPreservesLoopsAsync(0)),
        ("Timed skips reset their deadline each loop", () => SkipPreservesLoopsAsync(1)),
        ("Infinite macro loops with skipped steps can stop", SkipInfiniteLoopStopsAsync),
        ("Immediate skip does not bypass a found image", () => FoundImageIsNotSkippedAsync(0)),
        ("A match during the timeout executes immediately", () => FoundImageIsNotSkippedAsync(300)),
        ("Immediate skip preserves invalid-template errors", () => SkipPreservesErrorsAsync(0, MatchFailureReason.InvalidTemplate)),
        ("Timed skip preserves invalid-template errors", () => SkipPreservesErrorsAsync(300, MatchFailureReason.InvalidTemplate)),
        ("Immediate skip preserves capture errors", () => SkipPreservesErrorsAsync(0, MatchFailureReason.CaptureFailed)),
        ("Timed skip preserves capture errors", () => SkipPreservesErrorsAsync(300, MatchFailureReason.CaptureFailed)),
        ("Immediate skip preserves matching errors", () => SkipPreservesErrorsAsync(0, MatchFailureReason.MatchingError)),
        ("Timed skip preserves matching errors", () => SkipPreservesErrorsAsync(300, MatchFailureReason.MatchingError)),
        ("Empty image paths are never skipped", EmptyImageIsNotSkippedAsync),
        ("Stop during a single attempt takes priority over skipping", () => StopDuringMissingMatchAsync(0)),
        ("Stop during a timed attempt takes priority over skipping", () => StopDuringMissingMatchAsync(300)),
        ("Manual stop interrupts a 300-second search", StopDuringTimedSearchAsync),
        ("Polling delay never extends a finite timeout", PollingDelayRespectsTimeoutAsync),
        ("Skip settings survive cloning and JSON round trips", SkipSettingsRoundTripAsync),
        ("Legacy macros keep stop-on-miss defaults", LegacySkipDefaultsAsync),
        ("Timeout values are bounded to 0-300", SkipTimeoutBoundsAsync)
    ];

    private static MacroProfile CreateSkipProfile(int seconds)
    {
        var profile = CreateProfile(waitUntilFound: false);
        var action = (FindImageAction)profile.Actions[0];
        action.SkipIfNotFound = true;
        action.SkipTimeoutSeconds = seconds;
        return profile;
    }

    private static async Task SkipContinuesAsync(int seconds)
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture) { FindImageIntervalMs = 10 };
        var profile = CreateSkipProfile(seconds);
        profile.Actions.Add(new WaitAction { WaitSeconds = 0 });
        var events = new List<MacroExecutionEventArgs>();
        executor.ActionExecuted += (_, e) => events.Add(e);
        var result = await RunToCompletionAsync(executor, profile);

        Require(result.Success && executor.CompletedLoopCount == 1, "Skipping must complete the sequence.");
        Require(events.Count == 2 && events[0].Success && events[1].ActionIndex == 1,
            "A skip must advance to the next action.");
        Require(events[0].MatchResult is { Found: false }, "A skipped match must not be reported as found.");
        Require(events[0].Message == LocalizationService.Current.Format("ExecutorImageSkipped", seconds),
            "Skipping must have an explicit status message.");
        if (seconds == 0)
            Require(matcher.Calls == 1, "Zero seconds must perform exactly one attempt.");
        else
            Require(matcher.Calls > 1 && events[0].MatchResult is
                { FailureReason: MatchFailureReason.TimedOut, ElapsedMilliseconds: >= 1000 },
                "Timed skipping must keep searching until its deadline.");
    }

    private static async Task SkipPreservesLoopsAsync(int seconds)
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture) { FindImageIntervalMs = 10 };
        var profile = CreateSkipProfile(seconds);
        profile.Actions.Insert(0, new WaitAction { WaitSeconds = 0 });
        profile.LoopEnabled = true;
        profile.LoopCount = 3;
        profile.LoopIntervalMs = 80;
        var events = new List<(int Index, long Time)>();
        var stopwatch = Stopwatch.StartNew();
        executor.ActionExecuted += (_, e) => events.Add((e.ActionIndex, stopwatch.ElapsedMilliseconds));

        var result = await RunToCompletionAsync(executor, profile);
        Require(result.Success && executor.CompletedLoopCount == 3, "The original finite loop count must apply.");
        Require(events.Select(e => e.Index).SequenceEqual(new[] { 0, 1, 0, 1, 0, 1 }),
            "Skipping the final action must return to the first action of each loop.");
        for (var loop = 0; loop < 3; loop++)
        {
            Require(events[loop * 2 + 1].Time - events[loop * 2].Time >= seconds * 1000,
                "Every loop must get a fresh search deadline.");
            if (loop > 0)
                Require(events[loop * 2].Time - events[loop * 2 - 1].Time >= 70,
                    "The original loop interval must be preserved.");
        }
        if (seconds == 0)
            Require(matcher.Calls == 3, "Immediate skip must try once per loop.");
    }

    private static async Task SkipInfiniteLoopStopsAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture);
        var profile = CreateSkipProfile(0);
        profile.Actions.Insert(0, new WaitAction { WaitSeconds = 0 });
        profile.LoopEnabled = true;
        profile.LoopCount = 0;
        profile.LoopIntervalMs = 20;
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.ActionExecuted += (_, e) =>
        {
            if (e.ActionIndex == 1 && matcher.Calls >= 3)
                progressed.TrySetResult();
        };
        var completion = ObserveCompletion(executor);
        try
        {
            Require(executor.Start(profile), "The macro did not start.");
            await progressed.Task.WaitAsync(Deadline);
            Require(!completion.IsCompleted, "An infinite macro must keep looping after a skip.");
            executor.Stop();
            RequireCancelled(await completion.WaitAsync(Deadline), executor);
            Require(executor.CompletedLoopCount >= 3, "Skipped steps must count toward completed loops.");
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }

    private static async Task FoundImageIsNotSkippedAsync(int seconds)
    {
        using var capture = new ScreenCaptureService();
        var expectedCalls = seconds == 0 ? 1 : 3;
        using var matcher = new FakeMatcher(capture, call => call == expectedCalls
            ? new MatchResult { Found = true } : MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture) { FindImageIntervalMs = 10 };
        var result = await RunToCompletionAsync(executor, CreateSkipProfile(seconds));

        // The unsupported operation safely proves the normal action path was reached without clicking.
        Require(!result.Success && result.MatchResult is { Found: true }, "A found image must execute its operation.");
        Require(result.Message.Contains(LocalizationService.Current.Format("ExecutorUnknownOperation", "test-no-input")),
            "A successful match must not silently skip the action.");
        Require(matcher.Calls == expectedCalls, "Search must stop at the first successful match.");
    }

    private static async Task SkipPreservesErrorsAsync(int seconds, MatchFailureReason reason)
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => new MatchResult { FailureReason = reason });
        using var executor = new MacroExecutor(matcher, capture);
        var profile = CreateSkipProfile(seconds);
        profile.Actions.Add(new WaitAction { WaitSeconds = 0 });
        var result = await RunToCompletionAsync(executor, profile);
        Require(!result.Success && result.MatchResult?.FailureReason == reason,
            "Skip must preserve the original error.");
        Require(matcher.Calls == 1 && executor.CompletedLoopCount == 0 && executor.CurrentActionIndex == 0,
            "An error must stop immediately without advancing or retrying.");
    }

    private static async Task EmptyImageIsNotSkippedAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        using var executor = new MacroExecutor(matcher, capture);
        var profile = CreateSkipProfile(300);
        ((FindImageAction)profile.Actions[0]).ImagePath = string.Empty;
        var result = await RunToCompletionAsync(executor, profile);
        Require(!result.Success && matcher.Calls == 0, "An empty template path is an error, not a skippable miss.");
    }

    private static async Task StopDuringMissingMatchAsync(int seconds)
    {
        using var capture = new ScreenCaptureService();
        Action? stop = null;
        using var matcher = new FakeMatcher(capture, _ =>
        {
            stop!();
            return MatchResult.NotFound;
        });
        using var executor = new MacroExecutor(matcher, capture);
        stop = executor.Stop;
        var events = 0;
        executor.ActionExecuted += (_, _) => events++;
        RequireCancelled(await RunToCompletionAsync(executor, CreateSkipProfile(seconds)), executor);
        Require(events == 0 && matcher.Calls == 1, "A stopped action must not emit a successful skip.");
    }

    private static async Task StopDuringTimedSearchAsync()
    {
        using var capture = new ScreenCaptureService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var matcher = new FakeMatcher(capture, call =>
        {
            if (call >= 2) started.TrySetResult();
            return MatchResult.NotFound;
        });
        using var executor = new MacroExecutor(matcher, capture) { FindImageIntervalMs = 10 };
        var completion = ObserveCompletion(executor);
        try
        {
            Require(executor.Start(CreateSkipProfile(300)), "The macro did not start.");
            await started.Task.WaitAsync(Deadline);
            Require(!completion.IsCompleted, "A 300-second timeout must not skip early.");
            executor.Stop();
            RequireCancelled(await completion.WaitAsync(Deadline), executor);
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }

    private static async Task PollingDelayRespectsTimeoutAsync()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        var result = await matcher.WaitForImageAsync("unused.png", timeoutMs: 40, intervalMs: 2000).WaitAsync(Deadline);
        Require(result.FailureReason == MatchFailureReason.TimedOut && result.ElapsedMilliseconds is >= 40 and < 1500,
            "The last polling delay must be capped by the remaining search time.");
    }

    private static Task SkipSettingsRoundTripAsync()
    {
        var profile = CreateSkipProfile(137);
        var clone = profile.DeepClone();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MacroActionConverter());
        var reloaded = JsonSerializer.Deserialize<MacroProfile>(JsonSerializer.Serialize(profile, options), options)!;
        foreach (var copy in new[] { clone, reloaded })
        {
            Require(copy.Actions[0] is FindImageAction { SkipIfNotFound: true, SkipTimeoutSeconds: 137 },
                "Cloning and saving must preserve both skip settings.");
            Require(!ReferenceEquals(profile.Actions[0], copy.Actions[0]), "Copies must not share mutable actions.");
        }
        return Task.CompletedTask;
    }

    private static Task LegacySkipDefaultsAsync()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MacroActionConverter());
        var profile = JsonSerializer.Deserialize<MacroProfile>(
            """{"Actions":[{"Type":"FindImage","ImagePath":"unused.png","WaitUntilFound":false}]}""", options)!;
        Require(profile.Actions[0] is FindImageAction { SkipIfNotFound: false, SkipTimeoutSeconds: 0 },
            "Old JSON must retain stop-on-miss defaults.");
        return Task.CompletedTask;
    }

    private static Task SkipTimeoutBoundsAsync()
    {
        foreach (var (input, expected) in new[] { (-1, 0), (0, 0), (1, 1), (300, 300), (301, 300), (int.MaxValue, 300) })
        {
            var action = new FindImageAction { SkipTimeoutSeconds = input };
            Require(action.SkipTimeoutSeconds == expected, "Timeout setters must clamp to 0-300.");
            var reloaded = JsonSerializer.Deserialize<FindImageAction>($"{{\"SkipTimeoutSeconds\":{input}}}")!;
            Require(reloaded.SkipTimeoutSeconds == expected, "Imported timeouts must also clamp to 0-300.");
        }
        return Task.CompletedTask;
    }

    private static async Task<MacroExecutionEventArgs> RunToCompletionAsync(MacroExecutor executor, MacroProfile profile)
    {
        var completion = ObserveCompletion(executor);
        try
        {
            Require(executor.Start(profile), "The macro did not start.");
            return await completion.WaitAsync(Deadline);
        }
        finally
        {
            executor.Stop();
            await completion.WaitAsync(Deadline);
        }
    }
}
