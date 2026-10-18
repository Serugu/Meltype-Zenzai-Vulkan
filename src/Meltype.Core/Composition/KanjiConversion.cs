// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Composition;

/// <summary>文節 1 つ分の変換結果。Reading はその文節の読み (ひらがな)。</summary>
public sealed record ConversionClause(string Reading, string Text);

/// <summary>
/// ひらがな → 漢字かな混じりの変換。Meltype 自身は変換の辞書を持たず、OS や外部の変換エンジンを借りる
/// (Windows: Microsoft IME、Mac / Linux: Mozc など)。
/// </summary>
public interface IKanjiConverter
{
    /// <summary>全体を 1 回で変換する。変換できなければ null。</summary>
    string? Convert(string hiragana);

    /// <summary>
    /// 文節に区切って変換する。区切れなければ null。context は直前の確定済みの文字列 (変換の文脈として使うだけで、結果には含めない)。
    /// </summary>
    IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null);
}

/// <summary>
/// ライブ変換 (打つたびの変換) で、結果があとから良くなる変換エンジン (Mozc の候補を Zenzai で選び直すとき)。
/// ライブ変換の間は <see cref="Live"/> を true にすると、待つ時間を短くして Mozc の結果を先に返し、良い結果が届いたら <see cref="Updated"/> で知らせる
/// (受け取った側は、ライブ変換の表示を作り直す。もう一度変換すると良い結果が返る)。
/// </summary>
public interface ILiveConversionAware
{
    bool Live { get; set; }

    /// <summary>あとから良い結果が届いた (UI スレッドとは限らない)。</summary>
    event Action? Updated;

    /// <summary>
    /// 文節の区切り (各文節の読み) だけを返す。区切りを比べるだけなら、選び直し (Zenzai) を頼まない。
    /// 区切りを返せない (Zenzai を使っていない) ときは null。
    /// </summary>
    IReadOnlyList<string>? SegmentReadings(string hiragana, string? context);
}

/// <summary>
/// 変換の文脈 (ConvertClauses の context。最大 10 文字) より長い前後の文を使える変換エンジン (Zenzai)。
/// 変換を頼む前に、同じ文の中のキャレットの前と後ろの文字を渡す (無ければ null)。
/// </summary>
public interface ISurroundingsAware
{
    void SetSurroundings(string? before, string? after);
}

/// <summary>確定した変換を覚えられる変換エンジン (Mozc)。次から同じ読みで同じ文字列を先に出す。</summary>
public interface ILearningConverter
{
    /// <summary>ユーザーが確定した文節 (読みと文字列、区切りも確定したとおり) を覚えさせる。context は直前の確定済みの文字列。</summary>
    void Learn(string? context, IReadOnlyList<ConversionClause> clauses);
}
