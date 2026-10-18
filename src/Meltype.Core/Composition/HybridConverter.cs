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
    Func<string, string?>? learned = null)
    : IKanjiConverter, ILearningConverter, ILiveConversionAware
{
    private bool UseMozc => engine() != ConversionEngine.System && mozc is { IsAvailable: true };
    private bool UseSystem => engine() != ConversionEngine.Mozc || mozc is not { IsAvailable: true };
    private bool UseZenzai => UseMozc && zenzai is not null && (useZenzai?.Invoke() ?? false);

    /// <summary>ライブ変換の間は true (Zenzai を短く待ち、間に合わなければ <see cref="Updated"/> で知らせる)。</summary>
    public bool Live { get; set; }

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
            .Select((list, i) => Learned(segments[i].Reading, Plausible(segments[i].Reading, list))).ToList();
        if (zenzai!.Choose(context, hiragana, candidates, Live) is not { } picks || picks.Length != segments.Count) return null;
        var clauses = new List<ConversionClause>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            if (picks[i] >= candidates[i].Count) return null;
            clauses.Add(new ConversionClause(segments[i].Reading, candidates[i][picks[i]]));
        }
        return clauses;
    }

    /// <summary>
    /// ユーザーが前に選び直した語 (conversions.json) を 1 番目の候補にして zenz に渡す。zenz は Mozc の順位も点数に入れるので、
    /// 覚えた語が選ばれやすくなり、ほかの文節もその語に合わせて選ばれる (選んだあとにも、変換ボックスが覚えた語を最初の候補にする)。
    /// </summary>
    private IReadOnlyList<string> Learned(string reading, IReadOnlyList<string> candidates) => PreferLearned(learned?.Invoke(reading), candidates);

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
