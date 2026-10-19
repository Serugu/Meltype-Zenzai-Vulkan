// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Config;

namespace Meltype.Composition;

/// <summary>
/// Mozc と OS の変換エンジン (Windows では Microsoft IME) を組み合わせる。
/// 両方 (既定) なら Mozc で変換し、Mozc が使えない・変換できないときは OS の変換エンジンで変換する。
/// 候補の一覧は Mozc の候補の後ろに OS の変換エンジンの候補を足す (Microsoft IME に登録した単語も出る)。
/// Zenzai が有効なら、Mozc の文節ごとの候補から zenz が組み合わせを選び直す (選べなければ Mozc の 1 番目の候補)。
/// </summary>
public sealed class HybridConverter(Func<ConversionEngine> engine, MozcConverter? mozc, IKanjiConverter system,
    Func<string, IReadOnlyList<string>>? systemCandidates, ZenzaiReranker? zenzai = null, Func<bool>? useZenzai = null,
    Func<string, string?>? learned = null, Func<string, string?>? readingOf = null)
    : IKanjiConverter, ILearningConverter, ILiveConversionAware, ISurroundingsAware
{
    /// <summary>
    /// zenz に候補に縛られず変換させた結果 (読みを逆変換で確かめたもの) を使うのは、その点数が選び直しの答えより
    /// これだけ高いとき (zenz の対数確率)。AJIMEE-Bench と tools/zenzai-*.txt で決めた (差 0〜1 が同じくらいで、2 以上は使う回数が減って正解も減る)。
    /// 測るときに変えられる (float.MaxValue で使わない)。readingOf (漢字の読みを求める逆変換) を渡さなければ、自由な変換は使わない。
    /// </summary>
    public float FreeMargin { get; set; } = 1f;

    /// <summary>
    /// 自由な変換の読みの確かめ方 (測るときに切り替える)。候補で作れるかを先に見ると、逆変換だけより AJIMEE-Bench と
    /// tools/zenzai-*.txt の 320 問で 266 → 270 問正解 (差 1。答えが変わった 17 問で、正解から外れたものは無し)。
    /// </summary>
    public FreeCheck FreeCheckMode { get; set; } = FreeCheck.CandidatesOrReading;

    public enum FreeCheck
    {
        /// <summary>漢字の読みを逆変換で確かめる (逆変換の読み違いで、正しい答えも捨てることがある: 橋 → ばし)。</summary>
        Reading,
        /// <summary>Mozc の文節ごとの候補でつなげて作れるか (作れれば読みは合っている) を先に見て、だめなら逆変換で確かめる。</summary>
        CandidatesOrReading,
        /// <summary>確かめない (読みと違う文も使ってしまう。測るときの上限)。</summary>
        None,
    }

    // zenz に渡す前後の文 (変換ボックスが変換を頼む前に入れる。Mozc に渡す文脈より長い)
    private string? _before, _after;

    public void SetSurroundings(string? before, string? after)
    {
        _before = before;
        _after = after;
    }

    private bool UseMozc => engine() != ConversionEngine.System && mozc is { IsAvailable: true };
    private bool UseSystem => engine() != ConversionEngine.Mozc || mozc is not { IsAvailable: true };
    private bool UseZenzai => UseMozc && zenzai is not null && (useZenzai?.Invoke() ?? false);

    /// <summary>ライブ変換の間は true (Zenzai を短く待ち、間に合わなければ <see cref="Updated"/> で知らせる)。</summary>
    public bool Live { get; set; }

    /// <summary>
    /// ライブ変換の間は、Mozc の 1 番目がかなだけの文節を zenz に変えさせない (打ちかけの語を漢字にしない)。
    /// Space で変換したときは zenz が選ぶ。測るときに切り替えられるようにしてある。
    /// </summary>
    public bool LiveKeepsKana { get; set; } = true;

    public event Action? Updated
    {
        add { if (zenzai is not null) zenzai.ResultReady += value; }
        remove { if (zenzai is not null) zenzai.ResultReady -= value; }
    }

    /// <summary>Zenzai を使うときだけ、Mozc の区切りを返す (Mozc の結果は控えがあるので速い。選び直しは頼まない)。</summary>
    public IReadOnlyList<string>? SegmentReadings(string hiragana, string? context) =>
        UseZenzai && mozc!.Segments(hiragana, context) is { Count: > 0 } segments ? segments.Select(s => s.Reading).ToList() : null;

    public string? Convert(string hiragana) =>
        UseZenzai
            ? ConvertClauses(hiragana) is { Count: > 0 } clauses ? string.Concat(clauses.Select(c => c.Text)) : null
            : (UseMozc ? mozc!.Convert(hiragana) : null) ?? (UseSystem ? system.Convert(hiragana) : null);

    public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null) =>
        (UseZenzai ? ConvertWithZenzai(hiragana, context) : null)
        ?? (UseMozc ? mozc!.ConvertClauses(hiragana, context) : null)
        ?? (UseSystem ? system.ConvertClauses(hiragana, context) : null);

    /// <summary>Mozc の文節の区切りはそのままで、各文節の候補を zenz に選ばせる。選べなければ null。</summary>
    private List<ConversionClause>? ConvertWithZenzai(string hiragana, string? context)
    {
        if (mozc!.Segments(hiragana, context) is not { Count: > 0 } segments) return null;
        var candidates = segments.Select(s => s.Candidates.Count > 0 ? s.Candidates : [s.Reading])
            .Select((list, i) => Learned(segments[i].Reading, Plausible(segments[i].Reading, list)))
            // 打っている途中は語が切れていることが多く、Mozc はそういう文節をかなのまま出すが、zenz は漢字の候補を選びがち
            // (打っているそばから漢字になる)。ライブ変換の間は、かなのままの文節はかなのままにする
            .Select(list => Live && LiveKeepsKana && IsKanaOnly(list[0]) ? [list[0]] : list).ToList();
        // zenz の左の文脈は、変換ボックスが渡した長い文 (無ければ Mozc と同じ文脈)。仮の文脈 (に → これ + に) は Mozc 向けなので使わない
        var left = _before ?? (context == "これ" ? null : context);
        if (zenzai!.Choose(left, hiragana, candidates, Live, _after) is not { } picks || picks.Length != segments.Count) return null;
        var clauses = new List<ConversionClause>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            if (picks[i] >= candidates[i].Count) return null;
            clauses.Add(new ConversionClause(segments[i].Reading, candidates[i][picks[i]]));
        }
        return Live ? clauses : WithFreeConversion(left, hiragana, clauses, segments) ?? clauses;
    }

    /// <summary>
    /// Space で変換したとき: zenz に候補に縛られず変換させ、Mozc の候補に無い答え (区切りの違い: 庭鶏 → 二羽鶏) を拾う。
    /// 漢字の部分の読みを逆変換 (Microsoft IME) で確かめ、読みが合い、zenz の点数が選び直しの答えより FreeMargin 以上高ければ使う。
    /// 使わないときは null。
    /// </summary>
    private List<ConversionClause>? WithFreeConversion(string? left, string hiragana, List<ConversionClause> chosen,
        IReadOnlyList<(string Reading, IReadOnlyList<string> Candidates)> segments)
    {
        if (readingOf is null || FreeMargin >= float.MaxValue) return null;
        var baseline = string.Concat(chosen.Select(c => c.Text));
        if (zenzai!.Generate(left, hiragana, _after, baseline) is not { } free || free.Text == baseline || free.Score < free.BaselineScore + FreeMargin) return null;
        var readings = chosen.Select(c => c.Reading).ToList();
        return FreeCheckMode switch
        {
            FreeCheck.CandidatesOrReading => AlignToCandidates(segments, free.Text) ?? AlignFree(free.Text, hiragana, readings, readingOf),
            FreeCheck.None => AlignToCandidates(segments, free.Text) ?? AlignFree(free.Text, hiragana, readings, readingOf) ?? [new ConversionClause(hiragana, free.Text)],
            _ => AlignFree(free.Text, hiragana, readings, readingOf),
        };
    }

    /// <summary>
    /// text を、Mozc の文節ごとに候補を 1 つずつ選んでつなげて作れるなら、Mozc の文節の区切りで返す (作れなければ null)。
    /// 候補はどれもその文節の読みの変換なので、作れれば読みは合っている (逆変換で確かめなくてよい)。順位の高い候補から試す。
    /// 文節の読みに半角の英数字があれば、CompositionController と同じく半角にそろえて比べる。
    /// 助詞だけの文節 (に・で など) は、かなでしか作らない (CompositionController がかなに戻すので、2 などにすると表示が text と違ってしまう)。
    /// </summary>
    internal static List<ConversionClause>? AlignToCandidates(IReadOnlyList<(string Reading, IReadOnlyList<string> Candidates)> segments, string text)
    {
        var lists = segments.Select(s => CompositionController.Particles.Contains(s.Reading)
            ? [s.Reading]
            : (s.Candidates.Count > 0 ? s.Candidates : [s.Reading]).Select(c => CompositionController.NormalizeHalfWidth(c, s.Reading)).ToList()).ToList();
        var chosen = new string[segments.Count];
        var dead = new HashSet<(int Segment, int Position)>();
        return Fill(0, 0) ? segments.Select((s, i) => new ConversionClause(s.Reading, chosen[i])).ToList() : null;

        // segment 番目から後ろの文節で、text の position 文字目から後ろを作れるか (作れなかった組は覚えて、もう試さない)
        bool Fill(int segment, int position)
        {
            if (segment == segments.Count) return position == text.Length;
            if (dead.Contains((segment, position))) return false;
            var reading = segments[segment].Reading;
            var ascii = reading.Any(char.IsAsciiLetterOrDigit);
            foreach (var candidate in lists[segment])
            {
                if (candidate.Length == 0 || position + candidate.Length > text.Length) continue;
                var matches = ascii
                    ? CompositionController.NormalizeHalfWidth(text.Substring(position, candidate.Length), reading) == candidate
                    : text.AsSpan(position, candidate.Length).SequenceEqual(candidate);
                if (!matches || !Fill(segment + 1, position + candidate.Length)) continue;
                chosen[segment] = candidate;
                return true;
            }
            dead.Add((segment, position));
            return false;
        }
    }

    /// <summary>
    /// zenz が自由に変換した文字列 (text) の読みが、入力の読みと合うかを確かめ、文節に分け直す。合わなければ null。
    /// 漢字の並びの読みは逆変換 (readingOf) で求め、それ以外の文字はそのまま (カタカナはひらがなにして) 読みと照らす。
    /// 文節の区切りは Mozc の区切り (segmentReadings) に合わせ、漢字の並びの読みが区切りをまたぐところは 1 つの文節にする。
    /// </summary>
    internal static List<ConversionClause>? AlignFree(string text, string reading, IReadOnlyList<string> segmentReadings, Func<string, string?> readingOf)
    {
        // 文字列を「漢字の並び」と「それ以外の 1 文字」に分け、それぞれの読みを求める
        var pieces = new List<(string Text, string Reading, bool Ideograph)>();
        for (var i = 0; i < text.Length;)
        {
            if (IsIdeograph(text[i]))
            {
                var end = i;
                while (end < text.Length && IsIdeograph(text[end])) end++;
                var run = text[i..end];
                if (readingOf(run) is not { Length: > 0 } runReading) return null;
                pieces.Add((run, ToHiragana(runReading).Replace(" ", "").Replace("\u3000", ""), true));
                i = end;
            }
            else
            {
                pieces.Add((text[i].ToString(), ToHiragana(text[i].ToString()), false));
                i++;
            }
        }
        var expected = ToHiragana(reading);
        if (string.Concat(pieces.Select(p => p.Reading)) != expected) return null;

        // Mozc の区切りの位置 (読みの何文字目か)。漢字の並びの読みの途中にある区切りは使わない
        var boundaries = new SortedSet<int>();
        var offset = 0;
        foreach (var segment in segmentReadings)
        {
            offset += segment.Length;
            boundaries.Add(offset);
        }
        if (offset != expected.Length) return null;
        var position = 0;
        foreach (var piece in pieces)
        {
            if (piece.Ideograph || piece.Reading.Length != piece.Text.Length) boundaries.RemoveWhere(b => b > position && b < position + piece.Reading.Length);
            position += piece.Reading.Length;
        }

        var clauses = new List<ConversionClause>();
        var clauseText = new System.Text.StringBuilder();
        var clauseStart = 0;
        position = 0;
        foreach (var piece in pieces)
        {
            clauseText.Append(piece.Text);
            position += piece.Reading.Length;
            if (!boundaries.Contains(position)) continue;
            clauses.Add(new ConversionClause(reading[clauseStart..position], clauseText.ToString()));
            clauseText.Clear();
            clauseStart = position;
        }
        return clauses;
    }

    private static bool IsIdeograph(char c) => c is >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' or '々' or '〆' or 'ヶ' or 'ヵ' or '〇';

    private static string ToHiragana(string text) =>
        new(text.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c).ToArray());

    /// <summary>
    /// ユーザーが前に選び直した語 (conversions.json) を 1 番目の候補にして zenz に渡す。zenz は Mozc の順位も点数に入れるので、
    /// 覚えた語が選ばれやすくなり、ほかの文節もその語に合わせて選ばれる (選んだあとにも、変換ボックスが覚えた語を最初の候補にする)。
    /// </summary>
    private IReadOnlyList<string> Learned(string reading, IReadOnlyList<string> candidates) => PreferLearned(learned?.Invoke(reading), candidates);

    internal static bool IsKanaOnly(string text) =>
        text.Length > 0 && text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    /// <summary>覚えた語が候補にあれば 1 番目にする (無ければそのまま)。</summary>
    internal static IReadOnlyList<string> PreferLearned(string? text, IReadOnlyList<string> candidates) =>
        text is null || candidates[0] == text || !candidates.Contains(text) ? candidates : [text, .. candidates.Where(c => c != text)];

    /// <summary>
    /// 読みの前の部分だけの候補を除く。Mozc の候補には、送り仮名を落とした候補 (はこぶ → 運ぶ・運) も混じり、
    /// zenz に選ばせると送り仮名が消える。ほかの候補から、読みの末尾のひらがなを落としただけの候補 (運ぶ → 運) を除く。
    /// (漢字だけの候補をまとめて除くと、かんじ → 漢字 のような正しい候補も消えるので、ほかの候補と比べて決める)
    /// </summary>
    internal static IReadOnlyList<string> Plausible(string reading, IReadOnlyList<string> candidates)
    {
        bool Truncated(string c) => candidates.Any(d =>
            d.Length > c.Length && d.StartsWith(c, StringComparison.Ordinal) && d[c.Length..] is var tail
            && tail.All(ch => ch is >= 'ぁ' and <= 'ゖ') && reading.EndsWith(tail, StringComparison.Ordinal));
        var kept = candidates.Where((c, i) => i == 0 || !Truncated(c)).ToList();
        return kept.Count == candidates.Count ? candidates : kept;
    }

    /// <summary>確定した文節を Mozc に覚えさせる (Microsoft IME の学習は Microsoft IME に任せる)。</summary>
    public void Learn(string? context, IReadOnlyList<ConversionClause> clauses)
    {
        if (!UseMozc) return;
        mozc!.Learn(context, clauses);
        // Mozc の候補の順番が変わるので、zenz の選んだ結果の控えも捨てる (順番は zenz の点数にも効く)
        zenzai?.ClearCache();
    }

    /// <summary>読みの候補の一覧 (Mozc → OS の変換エンジン)。</summary>
    public IReadOnlyList<string> Candidates(string reading)
    {
        var list = new List<string>();
        if (UseMozc) list.AddRange(mozc!.Candidates(reading));
        if (engine() != ConversionEngine.Mozc && systemCandidates is not null)
        {
            foreach (var candidate in systemCandidates(reading)) if (!list.Contains(candidate)) list.Add(candidate);
        }
        return list;
    }
}
