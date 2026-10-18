// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Serugu

using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>Zenzai (zenz で Mozc の候補を選び直す) のうち、ヘルパーを動かさずに確かめられるところ。</summary>
internal static class ZenzaiTests
{
    [Test]
    public static void Plausible_DropsCandidatesThatLostTheirOkurigana()
    {
        // Mozc は「はこぶ」の候補に、送り仮名を落とした「運」も出す。選ぶと送り仮名が消えるので除く
        var kept = HybridConverter.Plausible("はこぶ", ["運ぶ", "はこぶ", "運", "漕ぶ", "箱部", "ハコブ"]);
        Assert.Equal("運ぶ,はこぶ,漕ぶ,箱部,ハコブ", string.Join(",", kept));
    }

    [Test]
    public static void Plausible_KeepsKanjiOnlyCandidatesForTheWholeReading()
    {
        // 漢字だけの候補でも、読み全体に当たるもの (かんじ → 漢字) は残す
        var candidates = new[] { "感じ", "漢字", "幹事", "かんじ", "カンジ" };
        Assert.True(ReferenceEquals(candidates, HybridConverter.Plausible("かんじ", candidates)), "何も除かないときは同じ一覧を返す");
    }

    [Test]
    public static void PreferLearned_PutsTheLearnedWordFirst()
    {
        // 前に「観る」を選んでいたら、zenz に渡す候補でも 1 番目にする (Mozc の順位が点数に入るので選ばれやすくなる)
        Assert.Equal("観る,見る,診る", string.Join(",", HybridConverter.PreferLearned("観る", ["見る", "観る", "診る"])));
        // 覚えた語が候補に無い・覚えていないときはそのまま
        Assert.Equal("見る,診る", string.Join(",", HybridConverter.PreferLearned("観る", ["見る", "診る"])));
        Assert.Equal("見る,観る", string.Join(",", HybridConverter.PreferLearned(null, ["見る", "観る"])));
    }

    [Test]
    public static void Reranker_WithoutHelper_FallsBackToMozc()
    {
        var reranker = new ZenzaiReranker(Path.Combine(Path.GetTempPath(), "no-such-dir", "Meltype.Zenz.exe"),
            () => new ZenzaiOptions(Path.Combine(Path.GetTempPath(), "no-such-model.gguf"), "vulkan", 0, 80));
        Assert.True(!reranker.IsAvailable, "ヘルパーが無ければ使えない");
        Assert.True(reranker.Choose("", "きしゃ", [["記者", "汽車"]]) is null, "使えないときは null (呼び出し側は Mozc の 1 番目)");
        // 候補が 1 つずつしか無ければ、ヘルパーに頼まずに決まる
        Assert.Equal("0,0", string.Join(",", reranker.Choose("", "きょうは", [["今日"], ["は"]]) ?? []));
    }
}

/// <summary>--zenzai-check: 本物のヘルパーを、アプリと同じ待ち時間で動かして確かめる (調べる用。通常のテストには入れない)。</summary>
internal static class ZenzaiCheck
{
    public static int Run(string helper, string backend)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var options = new ZenzaiOptions(ZenzaiOptions.DefaultModelPath, backend, 0, 80);
        using var reranker = new ZenzaiReranker(helper, () => options);
        IReadOnlyList<IReadOnlyList<string>> request = [["感じ", "漢字", "幹事"], ["変換の", "返還の"], ["制度を", "精度を", "程度を"], ["上げる", "挙げる"]];
        string Show(int[]? picks) => picks is null ? "null (Mozc の結果を使う)" : string.Concat(picks.Select((p, i) => request[i][p]));
        var failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK  " : "NG  ")}{what}");
            if (!ok) failures++;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = reranker.Choose("", "かんじへんかんのせいどをあげる", request);
        Check(first is null, $"起動中は待たずに Mozc に戻る ({watch.ElapsedMilliseconds}ms): {Show(first)}");
        Check(reranker.WaitUntilReady(60000), $"起動した ({watch.ElapsedMilliseconds}ms)");

        var times = new List<double>();
        int[]? picks = null;
        var late = 0;
        for (var i = 0; i < 20; i++)
        {
            reranker.ClearCache();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            picks = reranker.Choose("", "かんじへんかんのせいどをあげる", request);
            times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (picks is null)
            {
                // 間に合わなかった (返事はあとで控えに入る)。人が次のキーを打つくらい待つ
                late++;
                Console.WriteLine($"    {i + 1} 回目: 間に合わず ({times[^1]:F1}ms)");
                Thread.Sleep(200);
            }
        }
        times.Sort();
        Check(Show(picks) == "漢字変換の精度を上げる" && late <= 1, $"選び直した: {Show(picks)}  (p50 {times[times.Count / 2]:F1}ms, 最大 {times[^1]:F1}ms, 間に合わず {late} 回)");

        // ヘルパーが落ちたとき: その回は Mozc に戻り、次に頼まれたら起動し直す
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Meltype.Zenz"))
        {
            try { if (string.Equals(process.MainModule?.FileName, Path.GetFullPath(helper), StringComparison.OrdinalIgnoreCase)) process.Kill(); } catch { }
            process.Dispose();
        }
        Thread.Sleep(300);
        reranker.ClearCache();
        var afterCrash = reranker.Choose("", "かんじへんかんのせいどをあげる", request);
        Check(afterCrash is null, $"落ちた直後は Mozc に戻る: {Show(afterCrash)}");
        Check(reranker.WaitUntilReady(60000), "起動し直した");
        reranker.ClearCache();
        var restarted = reranker.Choose("", "かんじへんかんのせいどをあげる", request);
        if (restarted is null)
        {
            Thread.Sleep(200);
            restarted = reranker.Choose("", "かんじへんかんのせいどをあげる", request);
        }
        Check(Show(restarted) == "漢字変換の精度を上げる", $"起動し直したあとも選び直せる: {Show(restarted)}");

        // ライブ変換: 待つ時間を 1ms にしてわざと間に合わなくし、結果が届いたら表示が差し替わることを確かめる (MELTYPE_MOZC が要る)
        if (Environment.GetEnvironmentVariable("MELTYPE_MOZC") is { Length: > 0 } mozcPath && File.Exists(mozcPath))
        {
            using var mozc = new MozcConverter(mozcPath, Path.Combine(Path.GetTempPath(), $"meltype-zenzai-check-{Environment.ProcessId}"));
            var liveOptions = options with { LiveTimeoutMs = 1 };
            using var live = new ZenzaiReranker(helper, () => liveOptions);
            Check(live.WaitUntilReady(60000), "ライブ変換用に起動した");
            var hybrid = new HybridConverter(() => Config.ConversionEngine.Mozc, mozc, new NoConverter(), null, live, () => true);
            using var updated = new ManualResetEventSlim();
            var updates = 0;
            hybrid.Updated += () => { Interlocked.Increment(ref updates); updated.Set(); };
            var keyboard = new CompositionTests.Keyboard(live: true, converter: hybrid, moreCandidates: hybrid.Candidates,
                userDictionary: new UserDictionary(null), translations: TranslationDictionary.Load());
            watch.Restart();
            keyboard.Type("kanjihenkannnoseidowoageru");
            var typedMs = watch.ElapsedMilliseconds;
            var shown = keyboard.Host.View?.Text;
            Check(shown is not null && shown != "漢字変換の精度を上げる", $"間に合わないうちは Mozc の結果を見せる: {shown} (26 キーで {typedMs}ms)");
            // 結果が届くたびに作り直す (作り直すと、その時点の読みでまた頼むので、何回かで落ち着く)
            for (var i = 0; i < 10 && keyboard.Host.View?.Text != "漢字変換の精度を上げる"; i++)
            {
                if (!updated.Wait(2000)) break;
                updated.Reset();
                keyboard.Controller.RefreshLiveConversion();
                Console.WriteLine($"    作り直し {i + 1}: {keyboard.Host.View?.Text}  ({live.Statistics})");
            }
            Check(keyboard.Host.View?.Text == "漢字変換の精度を上げる", $"結果が届いたら差し替わる: {keyboard.Host.View?.Text} (知らせ {updates} 回。{live.Statistics})");
        }
        else
        {
            Console.WriteLine("--  ライブ変換の差し替えは MELTYPE_MOZC が無いので確かめない");
        }
        return failures == 0 ? 0 : 1;
    }

    private sealed class NoConverter : IKanjiConverter
    {
        public string? Convert(string hiragana) => null;
        public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null) => null;
    }
}
