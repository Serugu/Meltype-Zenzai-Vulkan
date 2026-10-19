// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>区切りを点数で選ぶ (α版、設定「区切りを点数で選ぶ (α版)」) のテスト。</summary>
internal static class ScoredSegmentationTests
{
    private static readonly CompositionDetector Detector = CreateDetector(scored: true);

    /// <summary>同じ単語リストで、区切りを点数で選ばない (今までの FindSpans) 判定。両方で同じ結果になるか比べるとき用。</summary>
    private static readonly CompositionDetector PlainDetector = CreateDetector(scored: false);

    private static CompositionDetector CreateDetector(bool scored)
    {
        var detector = CompositionDetector.CreateDefault();
        detector.SpellChecker = Detection.BuiltInWordChecker.Shared;
        detector.UseScoredSegmentation = () => scored;
        return detector;
    }

    /// <summary>打って Enter で確定した文字列 (変換エンジンは使わないので、日本語はかなのまま)。</summary>
    private static string Type(string typed, CompositionDetector? detector = null)
    {
        var session = new MeltypeSession(detector ?? Detector, new CompositionTests.FakeConverter(), new CompositionOptions(), () => new Settings());
        var output = "";
        foreach (var character in typed + "\n")
        {
            var vk = character == '\n' ? VirtualKeys.Return : char.ToUpperInvariant(character);
            var result = session.HandleKey(vk, character == '\n' ? null : character, char.IsAsciiLetterUpper(character), false, false, false);
            foreach (var edit in result.Commits)
            {
                if (edit.DeleteBefore > 0) output = output[..(output.Length - Math.Min(edit.DeleteBefore, output.Length))];
                output += edit.Text;
            }
            if (!result.Consumed) output += character;
        }
        return output;
    }

    [Test]
    public static void ParticleHa_AfterEnglishWord()
    {
        // 英単語の最後の子音と は が 1 つの単位 (sha・tha・dha) になっても、英単語 + は に分ける。今までの区切りでは しゃ・てゃ・でゃ になっていた
        foreach (var (typed, expected) in new[] {
            ("soredemedalsha", "それでmedalsは"), ("tsukattepresidentha", "つかってpresidentは"), ("kinouhasnaredha", "きのうはsnaredは"),
            ("atodestraightsha", "あとでstraightsは") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void ParticleHa_KeepsJapaneseWords()
    {
        // よく使う日本語の語の中の しゃ は区切らない (dens|は・bus|は にしない)
        foreach (var (typed, expected) in new[] { ("densha", "でんしゃ"), ("shabushabu", "しゃぶしゃぶ"), ("kaisha", "かいしゃ"), ("kinouhadenshaninotta", "きのうはでんしゃにのった") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void LongWord_BeatsTwoShortWords()
    {
        // 長い 1 語 (string・glowers) を、短い語の組 (most + ring) や前の日本語を巻き込んだ並び (buglowers) より優先する
        foreach (var (typed, expected) in new[] { ("kyoumostringkamo", "きょうもstringかも"), ("zenbuglowerswo", "ぜんぶglowersを"), ("macOSnoupdate", "macOSのupdate"), ("seeyoulater", "seeyoulater") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void Acronym_HeadIsNotReadAsKana()
    {
        // 大文字に大文字が続く略語の頭 (IME の I) は、英語の区間にできるならかなにしない。
        // 辞書に無い略語 (IME) より、頭をかなにして残りを英単語にした区切り (い|ME) の点が高くなり、googleいME になっていた
        foreach (var (typed, expected) in new[] {
            ("IME", "IME"), ("googleIME", "googleIME"), ("theIMEis", "theIMEis"), ("KYOUHA", "KYOUHA"),
            ("kyouhaIMEwotsukau", "きょうはIMEをつかう"), ("AutoIMEnotesuto", "AutoIMEのてすと") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void AcronymBeforeCapitalizedWord_KeepsJapaneseTail()
    {
        // 辞書に無い略語 + 大文字で始まる語 + 日本語 (VPS|Hosting|が)。VPSHosting を略語 + ローマ字の打ち始め (AIde と同じ形) と見て
        // 大文字で始まる語の区切り (CapitalizedWordEnd) を飛ばし、略語の頭を日本語にしない決まり (Acronym_HeadIsNotReadAsKana) とあわせて
        // 全体が英語 (VPSHostingga) になっていた。最後の大文字から知っている語 (Hosting) で、後ろが助詞で始まるなら、略語 + 語として区切る
        foreach (var (typed, expected) in new[] {
            ("VPSHostingga", "VPSHostingが"), ("VPSHostingwotsukau", "VPSHostingをつかう"), ("XYZHostingga", "XYZHostingが"),
            ("EUHostingwotsukau", "EUHostingをつかう"), ("AWSConsolewo", "AWSConsoleを"), ("GPUCoreni", "GPUCoreに"), ("JSONFilewo", "JSONFileを"),
            ("TCPConnectionwo", "TCPConnectionを"), ("AWSLambdaga", "AWSLambdaが") })
            Assert.Equal(expected, Type(typed), typed);
        // 点数で選ばない今までの判定でも、同じ決まり (CapitalizedWordEnd) で区切る
        foreach (var (typed, expected) in new[] {
            ("VPSHostingga", "VPSHostingが"), ("XYZHostingwotsukau", "XYZHostingをつかう"), ("EUHostingwotsukau", "EUHostingをつかう"), ("APIServerga", "APIServerが") })
            Assert.Equal(expected, Type(typed, PlainDetector), typed + " (点数で選ばない判定)");
        // 語から後ろ全体が 1 つの英単語なら、短い語 (Car・Key) で区切らない。後ろが最後までローマ字として読めないなら区切らない (Red|mond)
        foreach (var (typed, expected) in new[] { ("ABCCartoon", "ABCCartoon"), ("ABCCarton", "ABCCarton"), ("AWSKeynote", "AWSKeynote"), ("ABCRedmond", "ABCRedmond"), ("ABCPlaymobil", "ABCPlaymobil") })
        {
            Assert.Equal(expected, Type(typed), typed);
            Assert.Equal(expected, Type(typed, PlainDetector), typed + " (点数で選ばない判定)");
        }
        // 語から後ろを続けてローマ字として読めるなら、略語 + 日本語の名前 (JR + Takeshi) と見て、頭の英単語 (Take・Kana) で区切らない
        foreach (var (typed, wrong) in new[] { ("JRTakeshi", "JRTakeし"), ("JRTakeshiga", "JRTakeしが"), ("JRKanagawa", "JRKanaがわ"), ("JRKanagawade", "JRKanaがわで") })
        {
            Assert.True(Type(typed) != wrong, $"{typed} を名前の途中で区切らない: {Type(typed)}");
            Assert.True(Type(typed, PlainDetector) != wrong, $"{typed} を名前の途中で区切らない (点数で選ばない判定): {Type(typed, PlainDetector)}");
        }
        // 略語の後ろが知っている語でない・後ろに大文字が続くものは今までどおり (UFO|catcher、ABC|Mo、Shinjuku|TV、全部大文字のローマ字)
        foreach (var (typed, expected) in new[] {
            ("UFOcatcher", "UFOcatcher"), ("ABCcatcher", "ABCcatcher"), ("ABCMo", "ABCMo"), ("ABCNi", "ABCNi"), ("IMEwo", "IMEを"),
            ("ABCShinjukuTV", "ABCShinjukuTV"), ("GENKIdesuKA", "GENKIdesuKA"), ("JRTokyoEki", "JRTokyoEki"), ("ABCDamenoTV", "ABCDamenoTV") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void CapitalFollowedByLowercase_StillScored()
    {
        // 小文字が続く大文字 (文の頭を Shift で打った Ga・Koreha) は、今までどおり点数で区切る (助詞 + 英単語を全部英語にしない)
        foreach (var (typed, expected) in new[] {
            ("Gagoogle", "がgoogle"), ("Hagithub", "はgithub"), ("Desupport", "でsupport"), ("Korehagithubni", "これはgithubに") })
            Assert.Equal(expected, Type(typed), typed);
    }
}
