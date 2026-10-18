// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Serugu

using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>
/// --zenzai-free &lt;Meltype.Zenz.exe&gt; &lt;評価データ (tools/zenzai-eval.py の .tsv)&gt;… :
/// Space で変換したときの、zenz の自由な変換 (読みを Microsoft IME の逆変換で確かめる) を使う・使わないで正解数を比べる。
/// 使うかどうかの差 (FreeMargin) をいくつか試す。native\mozc\bin の Mozc ヘルパーを使う (調べる用)。
/// </summary>
internal static class ZenzaiFreeCheck
{
    private static readonly float[] Margins = [float.MaxValue, 0f, 1f, 2f, 3f, 5f];

    public static int Run(string helper, IReadOnlyList<string> files)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var ime = new MsImeKanjiConverter();
        using var mozc = new MozcConverter(Path.GetFullPath(Path.Combine("native", "mozc", "bin", "meltype_mozc_helper.exe")), Path.Combine(Path.GetTempPath(), $"meltype-zenzai-free-{Environment.ProcessId}"));
        var options = new ZenzaiOptions(ZenzaiOptions.DefaultModelPath, "vulkan", 0, 5000, LiveTimeoutMs: 5000);
        using var zenzai = new ZenzaiReranker(helper, () => options);
        if (!zenzai.WaitUntilReady(60000)) return 1;
        var hybrid = new HybridConverter(() => Config.ConversionEngine.Mozc, mozc, new NoConverter(), null, zenzai, () => true, null, ime.Reading);

        foreach (var file in files)
        {
            var rows = File.ReadAllLines(file).Select(l => l.Split('\t')).Where(f => f.Length >= 4).ToList();
            var correct = new int[Margins.Length];
            var used = new int[Margins.Length];
            var examples = new List<string>();
            foreach (var fields in rows)
            {
                var (left, reading, answers) = (fields[0], fields[1], fields[3].Split('|'));
                var right = fields.Length > 4 ? fields[4] : "";
                string? beam = null;
                for (var m = 0; m < Margins.Length; m++)
                {
                    hybrid.FreeMargin = Margins[m];
                    hybrid.SetSurroundings(left.Length > 0 ? left : null, right.Length > 0 ? right : null);
                    // Mozc には Meltype と同じく 10 文字の文脈
                    var text = string.Concat(hybrid.ConvertClauses(reading, left.Length > 0 ? left[^Math.Min(10, left.Length)..] : null)?.Select(c => c.Text) ?? [reading]);
                    if (m == 0) beam = text;
                    else if (text != beam) used[m]++;
                    if (answers.Contains(text)) correct[m]++;
                    if (m == 3 && text != beam && examples.Count < 20) examples.Add($"  {(answers.Contains(text) ? "+" : answers.Contains(beam) ? "-" : "x")} {reading}: 選び直し={beam}  自由={text}  (正解 {answers[0]})");
                }
            }
            Console.WriteLine($"== {Path.GetFileName(file)} ({rows.Count} 問)");
            for (var m = 0; m < Margins.Length; m++)
            {
                Console.WriteLine($"  {(Margins[m] == float.MaxValue ? "自由な変換なし" : $"差 {Margins[m]}"),-14} 正解 {correct[m]}  自由な変換を使った {used[m]}");
            }
            foreach (var example in examples) Console.WriteLine(example);
        }
        return 0;
    }

    private sealed class NoConverter : IKanjiConverter
    {
        public string? Convert(string hiragana) => null;
        public IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? context = null) => null;
    }
}
