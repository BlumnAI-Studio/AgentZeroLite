using System.Diagnostics;
using System.IO;
using System.Text;

namespace Agent.Common.Voice;

/// <summary>
/// Local TTS via the SuperTonic Python CLI (pip install supertonic — Supertone Inc).
/// On-device ONNX runtime, ~99M params, 10 builtin voices (M1..M5, F1..F5),
/// 31 languages incl. Korean. M0020 — first AgentZero provider that shells out
/// to a pip-installed binary rather than calling a .NET library or HTTP API.
///
/// Subprocess shape: <c>{python} -m supertonic tts &lt;text&gt; --voice &lt;id&gt;
/// --lang &lt;lang&gt; --steps &lt;n&gt; -o &lt;tempfile.wav&gt;</c>. We invoke via
/// <c>python -m supertonic</c> rather than the bare <c>supertonic</c> shim so
/// Windows PATH ordering of the user's interpreter wins and we don't accidentally
/// pick up a stale entry point from a different Python install.
/// </summary>
public sealed class SuperTonicTts : ITextToSpeech
{
    public string ProviderName => "Supertonic";
    public string AudioFormat => "wav";

    /// <summary>Builtin voice ids shipped with Supertonic-3 (model card).</summary>
    public static readonly string[] BuiltinVoices =
        ["M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5"];

    /// <summary>
    /// ONNX inference steps, 5..12 (Supertonic default = 8). Higher = better
    /// quality, lower = faster. Mirrors the model's <c>--steps</c> flag.
    /// </summary>
    public int Steps { get; set; } = 8;

    /// <summary>
    /// BCP-47 short tag the CLI accepts (ko / en / ja / …). Empty falls back
    /// to <c>"na"</c> per the upstream documentation's "language not available"
    /// behaviour, which auto-detects from script.
    /// </summary>
    public string Language { get; set; } = "";

    private readonly string _pythonExe;
    private readonly IProcessRunner _runner;

    public SuperTonicTts(string pythonExe = "python", IProcessRunner? runner = null)
    {
        _pythonExe = string.IsNullOrWhiteSpace(pythonExe) ? "python" : pythonExe;
        _runner = runner ?? DefaultProcessRunner.Instance;
    }

    public Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(BuiltinVoices);

    /// <summary>
    /// One-time prerequisite check. Two-phase probe so we can distinguish
    /// "Python isn't reachable at all" from "Python is fine but supertonic
    /// isn't installed in *this* interpreter" — the second case is invisible
    /// to a single-shot <c>pip show</c> because the Windows Store stub at
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe</c> exits non-zero
    /// silently and would look identical to "package missing".
    ///
    /// Phase 1: <c>{python} -V</c> — confirms an interpreter resolves and
    /// reports its version. Failure here usually means PATH didn't update
    /// since AgentZero started, or no Python is installed at all.
    /// Phase 2: <c>{python} -m pip show supertonic</c> — confirms the package
    /// is installed in the same interpreter's site-packages.
    /// </summary>
    public async Task<bool> EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(_pythonExe) ? "python" : _pythonExe;
        progress?.Report($"Probing interpreter: {displayName} -V …");

        // ── Phase 1: Python itself reachable? ─────────────────────────
        string pythonVersion;
        try
        {
            var probe = await _runner.RunAsync(
                _pythonExe,
                new[] { "-V" },
                stdin: null,
                workingDir: null,
                ct);
            // python -V writes to stdout on 3.4+, but older / quirky shims
            // use stderr — accept either.
            var combined = (probe.StdOut + " " + probe.StdErr).Trim();
            if (probe.ExitCode != 0 || !combined.StartsWith("Python ", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(
                    $"Python not reachable as '{displayName}'. " +
                    "If you just installed Python, restart AgentZero so PATH refreshes, " +
                    "or paste the full path (e.g. C:\\Users\\<you>\\AppData\\Local\\Programs\\Python\\Python312\\python.exe).");
                return false;
            }
            pythonVersion = combined;
        }
        catch (Exception ex)
        {
            progress?.Report(
                $"Cannot launch '{displayName}': {ex.Message}. " +
                "Set the full python.exe path or restart AgentZero after installing Python.");
            return false;
        }

        // ── Phase 2: supertonic installed in *this* interpreter? ──────
        progress?.Report($"{pythonVersion} OK — checking supertonic package …");
        try
        {
            var res = await _runner.RunAsync(
                _pythonExe,
                new[] { "-m", "pip", "show", "supertonic" },
                stdin: null,
                workingDir: null,
                ct);
            if (res.ExitCode != 0 || string.IsNullOrEmpty(res.StdOut))
            {
                progress?.Report(
                    $"{pythonVersion} found, but supertonic is NOT installed in this interpreter. " +
                    $"Run: {displayName} -m pip install supertonic");
                return false;
            }
            var versionLine = res.StdOut
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase));
            progress?.Report(versionLine is null
                ? $"{pythonVersion} · Supertonic installed."
                : $"{pythonVersion} · Supertonic {versionLine}");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"pip show failed: {ex.Message}");
            return false;
        }
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var voiceId = string.IsNullOrWhiteSpace(voice) ? "F1" : voice.Trim();
        var lang = string.IsNullOrWhiteSpace(Language) ? "na" : Language.Trim();
        var steps = Math.Clamp(Steps, 5, 12);

        var tempWav = Path.Combine(Path.GetTempPath(), $"agentzero-supertonic-{Guid.NewGuid():N}.wav");
        var args = BuildArgs(text, voiceId, lang, steps, tempWav);

        try
        {
            var res = await _runner.RunAsync(_pythonExe, args, stdin: null, workingDir: null, ct);
            if (res.ExitCode != 0)
                throw new InvalidOperationException(
                    $"supertonic exited with code {res.ExitCode}. stderr: {Truncate(res.StdErr, 400)}");

            if (!File.Exists(tempWav))
                throw new InvalidOperationException(
                    $"supertonic exited 0 but produced no audio at {tempWav}. stderr: {Truncate(res.StdErr, 400)}");

            return await File.ReadAllBytesAsync(tempWav, ct);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    /// <summary>
    /// Command-line builder, factored out so tests can verify quoting without
    /// spawning a real process. Returns the full argv array passed to the
    /// chosen Python interpreter.
    /// </summary>
    internal static string[] BuildArgs(string text, string voice, string lang, int steps, string outputPath)
    {
        return new[]
        {
            "-m", "supertonic", "tts",
            text,
            "--voice", voice,
            "--lang", lang,
            "--steps", steps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o", outputPath,
        };
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}

/// <summary>
/// Subprocess invocation seam. <see cref="DefaultProcessRunner"/> shells out
/// via <see cref="Process"/>; tests inject a fake to record argv without
/// spawning anything. Lives next to <see cref="SuperTonicTts"/> because it's
/// the only consumer today — promote to its own file when a second caller
/// appears.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? stdin,
        string? workingDir,
        CancellationToken ct);
}

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr);

internal sealed class DefaultProcessRunner : IProcessRunner
{
    public static readonly DefaultProcessRunner Instance = new();

    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? stdin,
        string? workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessRunResult(p.ExitCode, stdout, stderr);
    }
}
