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
    public static void AlignFree_ChecksTheReadingAndSplitsAtMozcBoundaries()
    {
        // 逆変換の代わり (漢字の並び → 読み)
        static string? Reading(string text) => text switch { "庭" => "にわ", "二羽鶏" => "にわにわとり", "端" => "はし", "橋" => "はし", _ => null };
        // Mozc の区切り にわには|にわにわとりが|いる に合わせて分ける
        var clauses = HybridConverter.AlignFree("庭には二羽鶏がいる", "にわにはにわにわとりがいる", ["にわには", "にわにわとりが", "いる"], Reading);
        Assert.Equal("にわには=庭には|にわにわとりが=二羽鶏が|いる=いる", string.Join("|", clauses!.Select(c => $"{c.Reading}={c.Text}")));
        // 漢字の読みが Mozc の区切りをまたぐときは、1 つの文節にする (にわ|にわとり の区切りの真ん中に 二羽鶏)
        clauses = HybridConverter.AlignFree("二羽鶏がいる", "にわにわとりがいる", ["にわ", "にわとりが", "いる"], Reading);
        Assert.Equal("にわにわとりが=二羽鶏が|いる=いる", string.Join("|", clauses!.Select(c => $"{c.Reading}={c.Text}")));
        // 読みが合わない (zenz が読みと違う字を書いた)・逆変換できないときは使わない
        Assert.True(HybridConverter.AlignFree("庭には鶏がいる", "にわにはにわにわとりがいる", ["にわには", "にわにわとりが", "いる"], Reading) is null, "読みが合わない");
        Assert.True(HybridConverter.AlignFree("箸がある", "はしがある", ["はしが", "ある"], Reading) is null, "逆変換できない");
        // カタカナはひらがなにして照らす
        clauses = HybridConverter.AlignFree("橋のプログラム", "はしのぷろぐらむ", ["はしの", "ぷろぐらむ"], Reading);
        Assert.Equal("はしの=橋の|ぷろぐらむ=プログラム", string.Join("|", clauses!.Select(c => $"{c.Reading}={c.Text}")));
    }

    [Test]
    public static void AlignToCandidates_UsesOnlyMozcCandidates()
    {
        static string Joined(List<ConversionClause>? clauses) => clauses is null ? "(null)" : string.Join("|", clauses.Select(c => $"{c.Reading}={c.Text}"));
        // Mozc の文節ごとの候補でつなげて作れれば、その区切りで返す (候補はその読みの変換なので、読みは合っている)
        IReadOnlyList<(string, IReadOnlyList<string>)> segments = [("はしを", ["橋を", "箸を", "端を"]), ("わたる", ["渡る", "亘る"])];
        Assert.Equal("はしを=端を|わたる=渡る", Joined(HybridConverter.AlignToCandidates(segments, "端を渡る")));
        Assert.True(HybridConverter.AlignToCandidates(segments, "嘴を渡る") is null, "候補に無い字は使わない");
        Assert.True(HybridConverter.AlignToCandidates(segments, "橋を渡るよ") is null, "余る文字があれば使わない");
        // 助詞だけの文節は、かなでしか作らない (2 にすると、助詞をかなに戻す決まりで 荷物をに階に になる)
        IReadOnlyList<(string, IReadOnlyList<string>)> cargo = [("にもつを", ["荷物を"]), ("に", ["に", "2", "二"]), ("かいに", ["階に", "回に"]), ("はこぶ", ["運ぶ"])];
        Assert.True(HybridConverter.AlignToCandidates(cargo, "荷物を2階に運ぶ") is null, "助詞の文節を数字にしない");
        // 半角の数字の読みは、Mozc の全角の候補を半角にそろえて比べる
        IReadOnlyList<(string, IReadOnlyList<string>)> digits = [("15にちに", ["１５日に", "十五日に"]), ("きた", ["来た", "着た"])];
        Assert.Equal("15にちに=15日に|きた=着た", Joined(HybridConverter.AlignToCandidates(digits, "15日に着た")));
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

/// <summary>
/// --zenzai-live: ライブ変換のように読みを 1 文字ずつ伸ばして (4 文字から)、Zenzai なし・あり (かなの文節も変える)・あり (かなの文節は変えない)
/// の 3 通りで変換し、打っている途中で漢字が増える回数と、読み全体の正解数を比べる (MELTYPE_MOZC が要る。調べる用)。
/// 結果が毎回同じになるよう、選び直しは長めに待つ。
/// </summary>
internal static class ZenzaiLiveCheck
{
    public static int Run(string helper, IEnumerable<string> files)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (Environment.GetEnvironmentVariable("MELTYPE_MOZC") is not { Length: > 0 } mozcPath || !File.Exists(mozcPath))
        {
            Console.WriteLine("MELTYPE_MOZC に Mozc の変換ヘルパーの場所を入れてください");
            return 1;
        }
        using var mozc = new MozcConverter(mozcPath, Path.Combine(Path.GetTempPath(), $"meltype-zenzai-live-{Environment.ProcessId}"));
        var options = new ZenzaiOptions(ZenzaiOptions.DefaultModelPath, "vulkan", 0, 5000, LiveTimeoutMs: 5000);
        using var zenzai = new ZenzaiReranker(helper, () => options);
        if (!zenzai.WaitUntilReady(60000)) return 1;
        var hybrid = new HybridConverter(() => Config.ConversionEngine.Mozc, mozc, new NoConverter(), null, zenzai, () => true) { Live = true };

        List<ConversionClause> Clauses(string reading, bool zenz, bool keepKana)
        {
            hybrid.LiveKeepsKana = keepKana;
            return (zenz ? hybrid.ConvertClauses(reading) : mozc.ConvertClauses(reading))?.ToList() ?? [new ConversionClause(reading, reading)];
        }
        string Convert(string reading, bool zenz, bool keepKana) => string.Concat(Clauses(reading, zenz, keepKana).Select(c => c.Text));
        // Mozc ではかなのままの文節を、zenz が漢字などにした (区切りは同じなので、文節ごとに比べる)
        static bool KanaTurnedToKanji(List<ConversionClause> mozc, List<ConversionClause> zenz) =>
            mozc.Count == zenz.Count && mozc.Zip(zenz).Any(p => HybridConverter.IsKanaOnly(p.First.Text) && !HybridConverter.IsKanaOnly(p.Second.Text));
        static bool LastKanaTurnedToKanji(List<ConversionClause> mozc, List<ConversionClause> zenz) =>
            mozc.Count == zenz.Count && HybridConverter.IsKanaOnly(mozc[^1].Text) && !HybridConverter.IsKanaOnly(zenz[^1].Text);

        int prefixes = 0, moreKanjiOn = 0, moreKanjiFixed = 0, lastOn = 0, sentences = 0, offOk = 0, onOk = 0, fixedOk = 0;
        var examples = new List<string>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                var slash = line.IndexOf(" / ", StringComparison.Ordinal);
                if (slash < 0) continue;
                var answers = line[..slash].Split('|');
                var reading = line[(slash + 3)..].Trim();
                for (var length = 4; length <= reading.Length; length++)
                {
                    var prefix = reading[..length];
                    var offClauses = Clauses(prefix, false, false);
                    var onClauses = Clauses(prefix, true, false);
                    var keptClauses = Clauses(prefix, true, true);
                    var off = string.Concat(offClauses.Select(c => c.Text));
                    var on = string.Concat(onClauses.Select(c => c.Text));
                    var kept = string.Concat(keptClauses.Select(c => c.Text));
                    if (length < reading.Length)
                    {
                        prefixes++;
                        if (KanaTurnedToKanji(offClauses, onClauses))
                        {
                            moreKanjiOn++;
                            if (LastKanaTurnedToKanji(offClauses, onClauses)) lastOn++;
                            if (examples.Count < 15) examples.Add($"    {prefix}: なし={off}  あり={on}  直した後={kept}");
                        }
                        if (KanaTurnedToKanji(offClauses, keptClauses)) moreKanjiFixed++;
                    }
                    else
                    {
                        sentences++;
                        if (answers.Contains(off)) offOk++;
                        if (answers.Contains(on)) onOk++;
                        if (answers.Contains(kept)) fixedOk++;
                        if (answers.Contains(on) != answers.Contains(kept)) examples.Add($"  読み全体: {reading}: あり={on}  直した後={kept}");
                    }
                }
            }
        }
        Console.WriteLine($"打っている途中 {prefixes} 回のうち、Mozc ではかなの文節を zenz が漢字などにした回数: あり {moreKanjiOn} 回 (うち最後の文節 {lastOn} 回) / 直した後 {moreKanjiFixed} 回");
        foreach (var example in examples) Console.WriteLine(example);
        Console.WriteLine($"読み全体 (ライブ変換で Enter したときの変換エンジンの結果) の正解: なし {offOk}/{sentences}  あり {onOk}/{sentences}  直した後 {fixedOk}/{sentences}");
        return 0;
    }

    private sealed class NoConverter : IKanjiConverter
    {
        public string? Convert(string hiragana) => null;
        public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null) => null;
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
