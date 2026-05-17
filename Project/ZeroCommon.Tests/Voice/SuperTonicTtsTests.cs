using Agent.Common.Voice;

namespace ZeroCommon.Tests.Voice;

/// <summary>
/// Headless coverage for the pip-based Supertonic provider (M0020).
/// All filesystem + subprocess calls are mocked through <see cref="FakeProcessRunner"/>
/// so the test never requires Python or the supertonic package installed —
/// the suite must stay green on a fresh CI image.
/// </summary>
public sealed class SuperTonicTtsTests
{
    [Fact]
    public void BuiltinVoices_lists_ten_voices_in_M_then_F_order()
    {
        Assert.Equal(10, SuperTonicTts.BuiltinVoices.Length);
        Assert.Equal(new[] { "M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5" },
            SuperTonicTts.BuiltinVoices);
    }

    [Fact]
    public async Task GetAvailableVoicesAsync_returns_builtins()
    {
        var tts = new SuperTonicTts();
        var voices = await tts.GetAvailableVoicesAsync();
        Assert.Equal(SuperTonicTts.BuiltinVoices, voices);
    }

    [Fact]
    public void Builds_argv_with_module_invocation_and_quoted_text()
    {
        var args = SuperTonicTts.BuildArgs(
            text: "안녕하세요, 수퍼토닉입니다.",
            voice: "F3",
            lang: "ko",
            steps: 8,
            outputPath: @"C:\Temp\out.wav");

        Assert.Equal("-m", args[0]);
        Assert.Equal("supertonic", args[1]);
        Assert.Equal("tts", args[2]);
        Assert.Equal("안녕하세요, 수퍼토닉입니다.", args[3]);
        Assert.Equal("--voice", args[4]);
        Assert.Equal("F3", args[5]);
        Assert.Equal("--lang", args[6]);
        Assert.Equal("ko", args[7]);
        Assert.Equal("--steps", args[8]);
        Assert.Equal("8", args[9]);
        Assert.Equal("-o", args[10]);
        Assert.Equal(@"C:\Temp\out.wav", args[11]);
    }

    [Fact]
    public async Task EnsureReadyAsync_returns_true_when_python_and_pip_show_succeed()
    {
        var runner = new FakeProcessRunner();
        runner.Responses["-V"] = new ProcessRunResult(0, "Python 3.12.5\n", "");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(
            0, "Name: supertonic\nVersion: 1.2.3\nSummary: Lightning-fast on-device TTS\n", "");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.True(ok);
        // Success message should include both Python version and Supertonic version
        // so the user can sanity-check WHICH interpreter the package landed in.
        Assert.Contains(progress.Messages, m => m.Contains("Python 3.12.5") && m.Contains("1.2.3"));
    }

    [Fact]
    public async Task EnsureReadyAsync_distinguishes_python_unreachable_from_missing_package()
    {
        // Phase 1 fails: python -V exits with 9009 (Windows Store stub behaviour).
        var runner = new FakeProcessRunner();
        runner.Responses["-V"] = new ProcessRunResult(9009, "", "");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("Python not reachable") && m.Contains("restart AgentZero"));
        // Critically: we should NOT have advanced to phase 2 and falsely claim
        // "supertonic not installed" when the real problem is python itself.
        Assert.DoesNotContain(progress.Messages, m => m.Contains("pip install supertonic"));
    }

    [Fact]
    public async Task EnsureReadyAsync_reports_missing_package_when_python_works_but_pip_show_fails()
    {
        var runner = new FakeProcessRunner();
        runner.Responses["-V"] = new ProcessRunResult(0, "Python 3.12.5\n", "");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(1, "", "WARNING: Package(s) not found: supertonic\n");
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("supertonic is NOT installed") && m.Contains("pip install supertonic"));
    }

    [Fact]
    public async Task EnsureReadyAsync_accepts_python_minus_V_writing_to_stderr()
    {
        // Older / quirky Python builds emit "Python X.Y.Z" to stderr instead
        // of stdout. The probe should still recognise that as success.
        var runner = new FakeProcessRunner();
        runner.Responses["-V"] = new ProcessRunResult(0, "", "Python 3.7.9\n");
        runner.Responses["-m pip show supertonic"] = new ProcessRunResult(
            0, "Name: supertonic\nVersion: 1.0.0\n", "");
        var tts = new SuperTonicTts("python", runner);

        var ok = await tts.EnsureReadyAsync();

        Assert.True(ok);
    }

    [Fact]
    public async Task EnsureReadyAsync_returns_false_when_runner_throws_on_python_probe()
    {
        var runner = new FakeProcessRunner { ThrowOnRun = new System.ComponentModel.Win32Exception("not found") };
        var progress = new SyncProgress();
        var tts = new SuperTonicTts("python-missing", runner);

        var ok = await tts.EnsureReadyAsync(progress);

        Assert.False(ok);
        Assert.Contains(progress.Messages,
            m => m.Contains("Cannot launch 'python-missing'") && m.Contains("restart AgentZero"));
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public List<string> Messages { get; } = new();
        public void Report(string value) => Messages.Add(value);
    }

    [Fact]
    public async Task SynthesizeAsync_passes_voice_lang_steps_to_runner_and_reads_wav()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (file, args, _, _) =>
            {
                // Last arg is the temp wav path the provider chose — drop a fake WAV there
                // so SynthesizeAsync can read it back.
                var outPath = args[^1];
                System.IO.File.WriteAllBytes(outPath, new byte[] { 1, 2, 3, 4 });
            },
        };
        var tts = new SuperTonicTts("python", runner) { Steps = 10, Language = "ko" };

        var audio = await tts.SynthesizeAsync("hello", "M2");

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, audio);
        Assert.NotNull(runner.LastArgs);
        Assert.Contains("--voice", runner.LastArgs!);
        Assert.Contains("M2", runner.LastArgs!);
        Assert.Contains("--lang", runner.LastArgs!);
        Assert.Contains("ko", runner.LastArgs!);
        Assert.Contains("--steps", runner.LastArgs!);
        Assert.Contains("10", runner.LastArgs!);
    }

    [Fact]
    public async Task SynthesizeAsync_defaults_voice_F1_and_language_na_when_unset()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (_, args, _, _) => System.IO.File.WriteAllBytes(args[^1], new byte[] { 0 }),
        };
        var tts = new SuperTonicTts("python", runner); // Language unset

        await tts.SynthesizeAsync("ping", voice: "");

        Assert.Contains("F1", runner.LastArgs!);
        Assert.Contains("na", runner.LastArgs!);
    }

    [Fact]
    public async Task SynthesizeAsync_clamps_steps_to_valid_range()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 0,
            OnRun = (_, args, _, _) => System.IO.File.WriteAllBytes(args[^1], new byte[] { 0 }),
        };
        var tts = new SuperTonicTts("python", runner) { Steps = 99 };

        await tts.SynthesizeAsync("ping", "M1");

        // Clamp upper bound 12
        var stepsIdx = Array.IndexOf(runner.LastArgs!.ToArray(), "--steps");
        Assert.Equal("12", runner.LastArgs![stepsIdx + 1]);
    }

    [Fact]
    public async Task SynthesizeAsync_returns_empty_when_text_blank()
    {
        var runner = new FakeProcessRunner();
        var tts = new SuperTonicTts("python", runner);

        var audio = await tts.SynthesizeAsync("   ", "F1");

        Assert.Empty(audio);
        Assert.Null(runner.LastArgs); // no subprocess invoked
    }

    [Fact]
    public async Task SynthesizeAsync_throws_when_supertonic_exits_nonzero()
    {
        var runner = new FakeProcessRunner
        {
            ExitCode = 2,
            StdErr = "ModuleNotFoundError: supertonic",
        };
        var tts = new SuperTonicTts("python", runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
        Assert.Contains("exited with code 2", ex.Message);
        Assert.Contains("ModuleNotFoundError", ex.Message);
    }

    [Fact]
    public async Task SynthesizeAsync_throws_when_supertonic_returns_zero_but_no_audio_file()
    {
        var runner = new FakeProcessRunner { ExitCode = 0 }; // doesn't write the temp file
        var tts = new SuperTonicTts("python", runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tts.SynthesizeAsync("hi", "F1"));
    }

    /// <summary>
    /// Test-only IProcessRunner. Two ways to script behaviour: (a) per-argv
    /// <see cref="Responses"/> dictionary keyed by space-joined args — used by
    /// the EnsureReadyAsync two-phase tests so phase 1 (python -V) and phase 2
    /// (pip show) can return different things; (b) single-shot <see cref="ExitCode"/>
    /// / <see cref="StdOut"/> / <see cref="OnRun"/> — used by the SynthesizeAsync
    /// tests which only invoke the runner once.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public Exception? ThrowOnRun { get; set; }
        public Action<string, IReadOnlyList<string>, string?, string?>? OnRun { get; set; }
        public Dictionary<string, ProcessRunResult> Responses { get; } = new();

        public string? LastFileName { get; private set; }
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            string? stdin,
            string? workingDir,
            CancellationToken ct)
        {
            LastFileName = fileName;
            LastArgs = args;
            if (ThrowOnRun is not null) throw ThrowOnRun;
            OnRun?.Invoke(fileName, args, stdin, workingDir);

            var key = string.Join(' ', args);
            if (Responses.TryGetValue(key, out var scripted))
                return Task.FromResult(scripted);

            return Task.FromResult(new ProcessRunResult(ExitCode, StdOut, StdErr));
        }
    }
}
