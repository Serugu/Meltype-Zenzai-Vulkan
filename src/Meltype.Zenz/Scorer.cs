// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Meltype.Zenz;

/// <summary>
/// zenz で、文節ごとの候補の組み合わせを点数付けして選ぶ (azooKey の Zenzai の「候補の評価」を簡単にしたもの)。
///
/// プロンプト「 左の文脈  読み (カタカナ) 」を 1 回だけ計算し、その KV キャッシュを系列ごとに使い回す。
/// 文節ごとにビームサーチし、仮説の点数は「出力のトークンの対数確率の合計 − Mozc の順位 × rankWeight」。
/// 最後に文の終わり (&lt;/s&gt;) の確率も足す。1 つの文節の仮説は、まとめて 1 回のバッチで計算する。
/// </summary>
internal sealed class Scorer : IDisposable
{
    private const string InputTag = "";
    private const string OutputTag = "";
    private const string ContextTag = "";
    private const int MaxContextLength = 40;
    private const int MaxReadingLength = 120;
    private const int MaxPieceTokens = 24;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly LLamaBatch _batch = new();
    private readonly int _beam;
    private readonly int _maxCandidates;
    private readonly float _rankWeight;
    private readonly float _kanaPenalty;
    private readonly LLamaToken _eos;

    public string Backend { get; }

    private Scorer(LLamaWeights weights, LLamaContext context, Options options, string backend)
    {
        _weights = weights;
        _context = context;
        _beam = options.Beam;
        _maxCandidates = options.MaxCandidates;
        _rankWeight = options.RankWeight;
        _kanaPenalty = options.KanaPenalty;
        _eos = weights.Vocab.EOS ?? throw new InvalidOperationException("モデルに文の終わりのトークンがありません");
        Backend = backend;
    }

    public static Scorer Load(Options options)
    {
        if (!File.Exists(options.Model)) throw new FileNotFoundException($"モデルがありません: {options.Model}");
        var gpu = options.Backend is "vulkan" or "cuda";
        // 使うネイティブのライブラリは、最初に llama.cpp を呼ぶ前に決める (1 つのプロセスで 1 つだけ)。
        NativeLibraryConfig.All
            .WithCuda(options.Backend == "cuda")
            .WithVulkan(options.Backend == "vulkan")
            .WithAutoFallback(true)
            .WithLogCallback((level, message) =>
            {
                if (level >= LLamaLogLevel.Warning) Console.Error.Write(message);
            });

        var parameters = new ModelParams(options.Model)
        {
            // 系列 0..beam-1 がビーム、残りが「ビーム × 候補」の計算用。プロンプトの KV はすべての系列で共有する (KVUnified)。
            SeqMax = (uint)(options.Beam + options.Beam * options.MaxCandidates),
            KVUnified = true,
            ContextSize = (uint)options.ContextSize,
            BatchSize = (uint)options.BatchSize,
            UBatchSize = (uint)Math.Min(options.BatchSize, 512),
            GpuLayerCount = gpu ? 999 : 0,
            // KV キャッシュを 8bit にして VRAM を半分にする (V を量子化するには flash attention が要る)。
            FlashAttention = true,
            TypeK = options.QuantizedCache ? GGMLType.GGML_TYPE_Q8_0 : GGMLType.GGML_TYPE_F16,
            TypeV = options.QuantizedCache ? GGMLType.GGML_TYPE_Q8_0 : GGMLType.GGML_TYPE_F16,
            Threads = gpu ? 2 : Math.Max(2, Environment.ProcessorCount / 2),
            BatchThreads = gpu ? 2 : Math.Max(2, Environment.ProcessorCount / 2),
            Encoding = Encoding.UTF8,
            // mmap で読む。ファイルの中身は private のメモリにならず (測ると private が約 100MB 少ない)、
            // GPU へ送り終わったページは TrimWorkingSet で物理メモリから外せる
            LoadMode = options.MemoryMap ? LLamaLoadMode.MemoryMap : LLamaLoadMode.None,
            // zenz の pre-tokenizer「gpt2-small-japanese-char」は本家の llama.cpp が知らない名前で、読み込みに失敗する。
            // 中身は GPT-2 と同じ扱いでよい (azooKey 版の llama.cpp も同じ) ので、名前だけ差し替える。
            MetadataOverrides = { new LLama.Abstractions.MetadataOverride("tokenizer.ggml.pre", "gpt-2") },
        };
        var weights = LLamaWeights.LoadFromFile(parameters);
        var context = weights.CreateContext(parameters);
        var backend = gpu && NativeApi.llama_supports_gpu_offload() ? options.Backend : "cpu";
        return new Scorer(weights, context, options, backend);
    }

    private sealed record Hypothesis(int Sequence, float Score, int[] Picks, int Position, float[] Next);

    /// <summary>文節ごとに選んだ候補の番号。選べなければ null (呼び出し側は Mozc の 1 番目を使う)。</summary>
    public int[]? Choose(string leftContext, string reading, IReadOnlyList<IReadOnlyList<string>> segments)
    {
        if (segments.Count == 0 || segments.Any(s => s.Count == 0) || reading.Length > MaxReadingLength) return null;
        if (segments.All(s => s.Count == 1)) return new int[segments.Count];

        var handle = _context.NativeHandle;
        handle.MemoryClear(true);

        // プロンプト (zenz-v3 の形。左の文脈は末尾の 40 文字まで)
        var trimmed = leftContext.Length > MaxContextLength ? leftContext[^MaxContextLength..] : leftContext;
        var prompt = (trimmed.Length > 0 ? ContextTag + Preprocess(trimmed) : "") + InputTag + ToKatakana(Preprocess(reading)) + OutputTag;
        var promptTokens = _context.Tokenize(prompt, true, false);
        _batch.Clear();
        _batch.AddRange(promptTokens, 0, (LLamaSeqId)0, true);
        if (handle.Decode(_batch) != DecodeResult.Ok) return null;

        var beams = new List<Hypothesis> { new(0, 0, [], promptTokens.Length, LogSoftmax(handle.GetLogitsIth(promptTokens.Length - 1))) };
        var scratchBase = _beam;

        foreach (var segment in segments)
        {
            var candidates = segment.Take(_maxCandidates).ToList();
            var pieces = candidates.Select(c => _context.Tokenize(Preprocess(c), false, false)).ToList();
            _batch.Clear();
            // (ビーム, 候補) ごとに、計算用の系列へビームの KV をコピーしてから候補のトークンを足す
            var slots = new List<(Hypothesis Beam, int Candidate, int Sequence, int FirstIndex, int Count)>();
            for (var b = 0; b < beams.Count; b++)
            {
                for (var k = 0; k < candidates.Count; k++)
                {
                    var tokens = pieces[k];
                    if (tokens.Length == 0 || tokens.Length > MaxPieceTokens) continue;
                    var sequence = scratchBase + b * _maxCandidates + k;
                    handle.MemorySequenceCopy((LLamaSeqId)beams[b].Sequence, (LLamaSeqId)sequence, 0, beams[b].Position);
                    var first = _batch.TokenCount;
                    for (var i = 0; i < tokens.Length; i++) _batch.Add(tokens[i], beams[b].Position + i, (LLamaSeqId)sequence, true);
                    slots.Add((beams[b], k, sequence, first, tokens.Length));
                }
            }
            if (slots.Count == 0) return null;
            if (handle.Decode(_batch) != DecodeResult.Ok) return null;

            var scored = new List<Hypothesis>(slots.Count);
            // Mozc の 1 番目が漢字を含むのに、かなだけの候補 (やさしい・キレイ) を選ぶときは減点する
            // (zenz は話し言葉のかな書きを好みがちで、測るとかなに崩す誤りが多かった)
            var topHasKanji = !IsKanaOnly(candidates[0]);
            foreach (var (beam, k, sequence, first, count) in slots)
            {
                var tokens = pieces[k];
                var score = beam.Score + beam.Next[(int)tokens[0]] - _rankWeight * k
                    - (k > 0 && topHasKanji && IsKanaOnly(candidates[k]) ? _kanaPenalty : 0);
                for (var i = 1; i < count; i++) score += LogProbability(handle.GetLogitsIth(first + i - 1), tokens[i]);
                scored.Add(new Hypothesis(sequence, score, [.. beam.Picks, k], beam.Position + count, LogSoftmax(handle.GetLogitsIth(first + count - 1))));
            }

            // 上位のビームを残す: 古いビームの系列を消し、選んだ計算用の系列をビームの系列へ移す
            var kept = scored.OrderByDescending(h => h.Score).Take(_beam).ToList();
            for (var b = 0; b < _beam; b++) handle.MemorySequenceRemove((LLamaSeqId)b, -1, -1);
            beams = kept.Select((h, b) =>
            {
                handle.MemorySequenceCopy((LLamaSeqId)h.Sequence, (LLamaSeqId)b, -1, -1);
                return h with { Sequence = b };
            }).ToList();
            foreach (var (_, _, sequence, _, _) in slots) handle.MemorySequenceRemove((LLamaSeqId)sequence, -1, -1);
        }

        return beams.OrderByDescending(h => h.Score + h.Next[(int)_eos]).First().Picks;
    }

    /// <summary>最初の計算を済ませておく (GPU のシェーダーの用意など)。大きさの違うバッチを何回か流す。</summary>
    public void WarmUp()
    {
        Choose("", "きょうはいいてんきです", [["今日は", "京は"], ["いい", "良い"], ["天気です", "転機です"]]);
        Choose("会議の", "しりょうをじゅんびする", [["資料を", "史料を", "飼料を", "試料を"], ["準備する", "順備する"]]);
        // 候補の多い長い文 (大きいバッチ) も 1 回流す
        string[] many = ["記者の", "貴社の", "汽車の", "帰社の", "騎射の", "喜捨の", "キシャの", "きしゃの"];
        Choose("", "きしゃのきしゃのきしゃのきしゃのきしゃの", [many, many, many, many, many]);
    }

    private static bool IsKanaOnly(string text) =>
        text.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ');

    private static float LogProbability(ReadOnlySpan<float> logits, LLamaToken token) => logits[(int)token] - LogSumExp(logits);

    private static float[] LogSoftmax(ReadOnlySpan<float> logits)
    {
        var lse = LogSumExp(logits);
        var result = new float[logits.Length];
        for (var i = 0; i < logits.Length; i++) result[i] = logits[i] - lse;
        return result;
    }

    private static float LogSumExp(ReadOnlySpan<float> logits)
    {
        var max = float.NegativeInfinity;
        foreach (var v in logits) if (v > max) max = v;
        var sum = 0.0;
        foreach (var v in logits) sum += Math.Exp(v - max);
        return max + (float)Math.Log(sum);
    }

    // zenz のトークナイザーに合わせる (azooKey と同じ: 空白は全角空白、改行は使わない)
    private static string Preprocess(string text) => text.Replace(' ', '　').Replace('\n', '　').Replace('\r', '　');

    private static string ToKatakana(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++) if (chars[i] is >= 'ぁ' and <= 'ゖ') chars[i] = (char)(chars[i] + 0x60);
        return new string(chars);
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }
}
