using System.IO;
using Agent.Common.Llm;
using AgentZeroWpf.Services.Browser;
using Xunit.Abstractions;

namespace AgentTest;

/// <summary>
/// M0026 후속 #2 — integration test that analyzes a real YouTube link with
/// the operator's CURRENTLY-CONFIGURED LLM (not a hardcoded test model).
///
/// Flow mirrors the Agent Band plugin exactly:
///   URL → ParseYouTubeId → YouTubeOEmbedAsync (real network) →
///   ClassifyAsync (real LLM via LlmGateway).
///
/// The genre assertion (`!= 기타`) is the direct regression guard for the
/// "everything classified as 기타" report — that symptom was the LLM being
/// OFF, so this test loads the configured Local model first (or uses the
/// configured External backend) before classifying. It SKIPS (not fails)
/// when there is no network or no LLM set up, so it's safe to run anywhere
/// but does real work on the operator's machine.
/// </summary>
[Trait("Category", "YouTubeClassify")]
public sealed class YouTubeClassifyTests
{
    private readonly ITestOutputHelper _out;
    public YouTubeClassifyTests(ITestOutputHelper o) => _out = o;

    // The exact link the operator asked to analyze — note the trailing
    // &list=…&start_radio=1 radio params that must be ignored.
    private const string Url =
        "https://www.youtube.com/watch?v=UG05pxLGwzc&list=RDUG05pxLGwzc&start_radio=1";

    // Mirrors the plugin's YT_CATEGORIES (agent-band.js).
    private static readonly IReadOnlyList<string> Categories =
        new[] { "재즈", "K-Pop", "클래식", "힙합", "EDM", "발라드", "록", "OST", "기타" };

    [Fact]
    public void ParseYouTubeId_extracts_id_ignoring_list_and_radio_params()
    {
        Assert.Equal("UG05pxLGwzc", WebDevHost.ParseYouTubeId(Url));
        // Other URL forms the plugin must also handle.
        Assert.Equal("UG05pxLGwzc", WebDevHost.ParseYouTubeId("https://youtu.be/UG05pxLGwzc?t=10"));
        Assert.Equal("UG05pxLGwzc", WebDevHost.ParseYouTubeId("UG05pxLGwzc"));
        Assert.Null(WebDevHost.ParseYouTubeId("https://example.com/not-a-video"));
    }

    [SkippableFact]
    public async Task Classifies_real_link_with_configured_llm_and_is_not_etc()
    {
        var host = new WebDevHost();

        // 1) Analyze the link.
        var id = WebDevHost.ParseYouTubeId(Url);
        Assert.Equal("UG05pxLGwzc", id);

        // 2) oEmbed metadata (real network — skip if unreachable).
        var meta = await host.YouTubeOEmbedAsync(id!);
        _out.WriteLine($"oEmbed: ok={meta.Ok} title='{meta.Title}' author='{meta.Author}' err='{meta.Error}'");
        Skip.IfNot(meta.Ok && !string.IsNullOrWhiteSpace(meta.Title),
            $"oEmbed unavailable (network?): {meta.Error}");

        // 3) Ensure the CONFIGURED LLM is usable right now (load the saved Local
        //    model if it isn't loaded yet; External needs no load).
        await EnsureConfiguredLlmAsync();
        Skip.IfNot(LlmGateway.IsActiveAvailable(),
            "Configured LLM not available — open Settings → LLM and load a local model (or configure External), then re-run.");

        // 4) Classify with the real LLM.
        var r = await host.ClassifyAsync(meta.Title!, meta.Author, Categories);
        _out.WriteLine($"classify: ok={r.Ok} category='{r.Category}' raw='{r.Raw}' err='{r.Error}'");

        Assert.True(r.Ok, $"classify failed: {r.Error}");
        Assert.Contains(r.Category, Categories);
        // The regression guard: a music video (BABYMONSTER — DRIP) must land on
        // a real genre, not the catch-all. If this fires with the LLM loaded,
        // the configured model is too weak for genre classification (raw reply
        // is logged above) — that's the signal, not a test bug.
        Assert.False(r.Category == "기타",
            $"Expected a real genre (K-Pop) but got 기타. LLM raw reply: '{r.Raw}'.");
    }

    private static async Task EnsureConfiguredLlmAsync()
    {
        var s = LlmSettingsStore.Load();
        if (s.ActiveBackend == LlmActiveBackend.Local && LlmService.Llm is null)
        {
            var entry = LlmModelCatalog.FindById(s.ModelId);
            var path = LlmModelLocator.ResolveExistingOrTarget(entry);
            if (File.Exists(path))
                await LlmService.LoadAsync(s, path);
        }
        // External backend is stateless REST — nothing to load.
    }
}
