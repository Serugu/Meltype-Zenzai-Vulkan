// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Serugu

using System.Diagnostics;
using System.Globalization;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Meltype.Zenz;

/// <summary>
/// zenz (azooKey の Zenzai のかな漢字変換モデル、Apache-2.0) で、Mozc の候補を選び直すヘルパー。
/// Meltype から別のプロセスで動かし、標準入出力で 1 行ずつやり取りする (Mozc のヘルパーと同じ形)。
///
/// 起動: Meltype.Zenz.exe --model &lt;gguf&gt; [--backend vulkan|cuda|cpu] [--idle-exit 分]
/// 準備ができたら「READY 実際のバックエンド」、できなければ「ERROR 理由」を 1 行書く。
///
/// やり取り (UTF-8):
///   E[TAB]左の文脈[TAB]読み (ひらがなでもカタカナでもよい)[TAB]文節1の候補1[US]候補2…[RS]文節2の候補…[TAB]右の文脈 (無ければ省く)
///     → 文節ごとに選んだ候補の番号を [US] でつないだ 1 行 (選べなければ空行)
///   G[TAB]左の文脈[TAB]読み[TAB]右の文脈[TAB]選び直しの答え
///     → 候補に縛られずに zenz が変換した文字列[TAB]その点数[TAB]選び直しの答えの点数 (出せなければ空行。読みと合うかは呼び出し側で確かめる)
///   P → OK (生きているかの確認)
///   Q → 終了
/// 使わない時間が続いたら (--idle-exit)、自分で終了して VRAM・メモリを空ける。Meltype は次の変換で起動し直す。
/// </summary>
internal static class Program
{
    private const char UnitSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';

    private static long _lastUse = Environment.TickCount64;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        Scorer scorer;
        try
        {
            scorer = Scorer.Load(options);
        }
        catch (Exception ex)
        {
            stdout.WriteLine("ERROR " + ex.Message.ReplaceLineEndings(" "));
            return 1;
        }
        // GPU は最初の計算でシェーダーなどを用意するので数秒かかる。READY の前に済ませて、最初の変換で待たせない
        scorer.WarmUp();
        if (options.TrimWorkingSet) TrimWorkingSet();
        stdout.WriteLine("READY " + scorer.Backend);

        // 使わない時間が続いたら: 5 分で物理メモリを手放し (TrimWorkingSet)、--idle-exit 分で終了して VRAM も空ける。
        // 手放したあとの 1 回目は読み戻すぶん遅い (測ると 30ms → 約 80ms) ので、少し打つのを止めたくらいでは手放さない
        const long trimAfterMs = 5 * 60_000;
        var exitAfter = options.IdleExitMinutes > 0 ? TimeSpan.FromMinutes(options.IdleExitMinutes).TotalMilliseconds : double.PositiveInfinity;
        var trimmedAt = Interlocked.Read(ref _lastUse);
        var idleTimer = new Timer(_ =>
        {
            var last = Interlocked.Read(ref _lastUse);
            var idle = Environment.TickCount64 - last;
            if (idle > exitAfter) Environment.Exit(0);
            if (options.TrimWorkingSet && idle > trimAfterMs && trimmedAt != last)
            {
                trimmedAt = last;
                TrimWorkingSet();
            }
        }, null, 30_000, 30_000);
        GC.KeepAlive(idleTimer);

        if (options.Bench is { } benchFile) return Bench(scorer, benchFile, stdout);

        while (stdin.ReadLine() is { } line)
        {
            Interlocked.Exchange(ref _lastUse, Environment.TickCount64);
            if (line == "Q") break;
            if (line == "P")
            {
                stdout.WriteLine("OK");
                continue;
            }
            string reply;
            try
            {
                reply = line.StartsWith("E\t", StringComparison.Ordinal) ? Evaluate(scorer, line)
                    : line.StartsWith("G\t", StringComparison.Ordinal) ? Generate(scorer, line)
                    : "";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                reply = "";
            }
            stdout.WriteLine(reply);
        }
        scorer.Dispose();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>
    /// 読み込みと準備で使い終わったメモリ (重みを GPU へ送るときの一時的な領域など) を物理メモリから外す。
    /// 必要になればまた読み込まれるので、動きは変わらない。
    /// </summary>
    private static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (OperatingSystem.IsWindows())
        {
            using var self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
    }

    /// <summary>G[TAB]左の文脈[TAB]読み[TAB]右の文脈[TAB]選び直しの答え → 自由な変換[TAB]その点数[TAB]選び直しの答えの点数 (出せなければ空行)。</summary>
    private static string Generate(Scorer scorer, string line)
    {
        var fields = line.Split('\t');
        if (fields.Length < 5) return "";
        return scorer.Generate(fields[1], fields[2], fields[3], fields[4]) is { } result
            ? string.Join('\t', result.Text, result.Score.ToString("R", CultureInfo.InvariantCulture), result.BaselineScore.ToString("R", CultureInfo.InvariantCulture))
            : "";
    }

    private static string Evaluate(Scorer scorer, string line)
    {
        var fields = line.Split('\t');
        if (fields.Length < 4) return "";
        var segments = fields[3].Split(RecordSeparator).Select(r => (IReadOnlyList<string>)r.Split(UnitSeparator)).ToList();
        var picks = scorer.Choose(fields[1], fields[2], segments, fields.Length > 4 ? fields[4] : "");
        return picks is null ? "" : string.Join(UnitSeparator, picks);
    }

    /// <summary>
    /// --bench ファイル: 1 行に「文脈[TAB]読み[TAB]候補…」(Evaluate と同じ形、先頭の E は無し)。
    /// 1 行ずつの選んだ結果と、遅延 (p50 / p95 / 最大) を出す。
    /// </summary>
    private static int Bench(Scorer scorer, string file, StreamWriter stdout)
    {
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        // 1 回目は GPU の準備 (シェーダーの用意など) で遅いので数えない
        foreach (var l in lines.Take(3)) Evaluate(scorer, "E\t" + l);
        var times = new List<double>();
        int zenzCorrect = 0, mozcCorrect = 0, graded = 0;
        foreach (var l in lines)
        {
            // 4 列目があれば正解 (| で区切って複数書ける)、5 列目があれば右の文脈
            var fields = l.Split('\t');
            var request = "E\t" + string.Join('\t', fields.Take(3)) + (fields.Length > 4 && fields[4].Length > 0 ? "\t" + fields[4] : "");
            var started = Stopwatch.GetTimestamp();
            var picks = Evaluate(scorer, request);
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var segments = fields[2].Split(RecordSeparator).Select(r => r.Split(UnitSeparator)).ToList();
            var chosen = picks.Length == 0 ? "(選べず)" : string.Concat(picks.Split(UnitSeparator).Select((p, i) => segments[i][int.Parse(p, CultureInfo.InvariantCulture)]));
            var mozc = string.Concat(segments.Select(s => s[0]));
            var mark = "  ";
            if (fields.Length > 3)
            {
                var answers = fields[3].Split('|');
                graded++;
                var z = answers.Contains(chosen);
                var m = answers.Contains(mozc);
                if (z) zenzCorrect++;
                if (m) mozcCorrect++;
                mark = z == m ? (z ? "  " : "x ") : (z ? "+ " : "- ");
            }
            stdout.WriteLine($"{mark}{fields[1]} → {chosen}{(chosen == mozc ? "" : $"  (Mozc: {mozc})")}  {times[^1]:F1}ms");
        }
        times.Sort();
        double P(double q) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(q * times.Count) - 1)];
        using var self = Process.GetCurrentProcess();
        if (graded > 0) stdout.WriteLine($"正解: zenz {zenzCorrect}/{graded}  Mozc {mozcCorrect}/{graded}");
        stdout.WriteLine($"backend={scorer.Backend} n={times.Count} p50={P(0.5):F1}ms p95={P(0.95):F1}ms max={times[^1]:F1}ms workingSet={self.WorkingSet64 / 1048576}MB private={self.PrivateMemorySize64 / 1048576}MB");
        return 0;
    }
}

internal sealed record Options(string Model, string Backend, int IdleExitMinutes, int Beam, int MaxCandidates, float RankWeight, string? Bench,
    int ContextSize, int BatchSize, bool QuantizedCache, bool MemoryMap, bool TrimWorkingSet, float KanaPenalty)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        int Int(string name, int fallback) => int.TryParse(Value(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;
        float Float(string name, float fallback) => float.TryParse(Value(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;
        return new Options(
            Value("--model") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meltype", "zenz", "zenz-v3.2-small-Q5_K_M.gguf"),
            (Value("--backend") ?? "vulkan").ToLowerInvariant(),
            Int("--idle-exit", 10),
            // 既定の値は AJIMEE-Bench (azooKey の変換の評価データ、200 問) と tools/zenzai-*.txt で決めた。
            // ビーム幅 4 と 2 で正解数はほぼ同じで、2 のほうが 2〜3 割速い
            Math.Clamp(Int("--beam", 2), 1, 16),
            Math.Clamp(Int("--candidates", 8), 1, 16),
            Float("--rank-weight", 0.75f),
            Value("--bench"),
            Math.Clamp(Int("--ctx", 1024), 256, 8192),
            Math.Clamp(Int("--batch", 1024), 64, 8192),
            Value("--cache") != "f16",
            !args.Contains("--no-mmap"),
            !args.Contains("--no-trim"),
            Float("--kana-penalty", 1f));
    }
}
