// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Serugu

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Meltype.Composition;

/// <summary>Zenzai (zenz のヘルパー) の起動のしかた。</summary>
/// <param name="ModelPath">zenz の重み (gguf)。</param>
/// <param name="Backend">vulkan / cpu。</param>
/// <param name="IdleExitMinutes">使わない時間がこれだけ続いたら、ヘルパーが終了して VRAM・メモリを空ける (0 なら終了しない)。</param>
/// <param name="TimeoutMs">Space で変換したときに、1 回の選び直しを待つ時間。間に合わなければ Mozc の結果を使う (変換は UI スレッドで動くので短くする)。</param>
/// <param name="LiveTimeoutMs">
/// ライブ変換 (打つたびの変換) で待つ時間。間に合わなければ Mozc の結果を見せ、zenz の結果が届いたら <see cref="ZenzaiReranker.ResultReady"/> で知らせる
/// (打つのを待たせない。測ると 1 回の選び直しは p50 約 11ms・p95 約 23ms なので、ほとんどは待つうちに間に合う)。
/// </param>
public sealed record ZenzaiOptions(string ModelPath, string Backend, int IdleExitMinutes, int TimeoutMs, int LiveTimeoutMs = 25)
{
    /// <summary>重みの既定の置き場所 (%LOCALAPPDATA%\Meltype\zenz\)。</summary>
    public static string DefaultModelPath => Path.Combine(Config.AppPaths.DataDirectory, "zenz", "zenz-v3.2-small-Q5_K_M.gguf");

    /// <summary>設定の値から作る。</summary>
    public static ZenzaiOptions From(Config.Settings settings) => new(DefaultModelPath,
        settings.ZenzaiBackend == Config.ZenzaiBackend.Cpu ? "cpu" : "vulkan", settings.ZenzaiIdleExitMinutes, settings.ZenzaiTimeoutMs);
}

/// <summary>
/// zenz (azooKey の Zenzai のかな漢字変換モデル) で、Mozc の文節ごとの候補から組み合わせを選び直す。
/// 同梱のヘルパー (zenz\Meltype.Zenz.exe、llama.cpp で GPU 推論) を別のプロセスで動かし、標準入出力で頼む。
///
/// やり取り (UTF-8、1 行ずつ): 「E[TAB]前の文字列[TAB]読み[TAB]文節1の候補1[US]候補2…[RS]文節2の候補…[TAB]後ろの文字列」を送ると
/// (後ろの文字列は無ければ省く)、
/// 文節ごとに選んだ候補の番号を [US] でつないだ 1 行が返る (選べなければ空行)。
/// ヘルパーの起動 (重みの読み込みと GPU の準備) はバックグラウンドで行い、そのあいだや、応答が遅い・失敗したときは null を返す
/// (呼び出し側は Mozc の 1 番目の候補を使う)。ヘルパーは使わない時間が続くと自分で終了するので、次に頼まれたら起動し直す。
/// </summary>
public sealed class ZenzaiReranker : IDisposable
{
    private const char UnitSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';
    private const int StartTimeoutMs = 60000;

    private readonly string _helperPath;
    private readonly Func<ZenzaiOptions> _options;
    private readonly object _gate = new();
    // 頼みの行 → 返事の行 (同じ読みはライブ変換で何度も頼まれるので控える)
    private readonly Dictionary<string, string?> _results = new(StringComparer.Ordinal);
    private Process? _process;
    private ZenzaiOptions? _startedWith;
    private Task? _starting;
    // 待ちきれなかった頼み (その頼みの控えのキーと、返事を読む Task)。返事が来たら控えに入れ、来るまでは次を頼まない
    private (string Key, Task<string?> Reply)? _pending;
    // _pending の返事が届いたら ResultReady で知らせるか (ライブ変換で、結果を返せなかった頼みがあった)
    private bool _wantsNotice;
    private int _failures;
    private bool _missingDependency;
    private bool _disposed;

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    private static bool IsLoaderFailure(int exitCode) => exitCode is unchecked((int)0xC0000135) or unchecked((int)0xC0000139) or unchecked((int)0xC000007B);

    /// <param name="helperPath">Meltype.Zenz の実行ファイル。</param>
    /// <param name="options">起動のしかた (設定が変わったら、次に頼むときに起動し直す)。</param>
    public ZenzaiReranker(string helperPath, Func<ZenzaiOptions> options)
    {
        _helperPath = helperPath;
        _options = options;
    }

    /// <summary>ヘルパーと重みがあるか。</summary>
    public bool IsInstalled => File.Exists(_helperPath) && File.Exists(_options().ModelPath);

    /// <summary>使えるか。何度も失敗したら諦める。</summary>
    public bool IsAvailable => IsInstalled && _failures < 3 && !_missingDependency;

    /// <summary>
    /// 待ちきれなかった選び直しの結果が、あとから届いた (UI スレッドとは限らない)。ライブ変換の表示を作り直すのに使う
    /// (もう一度同じ読みで頼むと、控えから結果が返る)。
    /// </summary>
    public event Action? ResultReady;

    // 調べる用の回数 (頼んだ・控えから返した・前の返事を待っていて頼めなかった・間に合わなかった・あとから届いて知らせた)
    private long _requests, _cacheHits, _busy, _late, _notified;

    /// <summary>調べる用の回数。</summary>
    public string Statistics => $"頼んだ {_requests}・控え {_cacheHits}・前の返事待ち {_busy}・間に合わず {_late}・あとから知らせた {_notified}";

    /// <summary>
    /// 文節ごとに選んだ候補の番号。準備中・間に合わない・失敗したときは null。
    /// candidates は文節ごとの候補 (Mozc の順。どの文節にも 1 つ以上ある)。
    /// live はライブ変換 (打つたびの変換) のとき。待つ時間を短くし、間に合わなければ結果が届いたときに <see cref="ResultReady"/> で知らせる。
    /// </summary>
    public int[]? Choose(string? context, string reading, IReadOnlyList<IReadOnlyList<string>> candidates, bool live = false, string? after = null)
    {
        if (candidates.Count == 0 || candidates.Any(c => c.Count == 0)) return null;
        if (candidates.All(c => c.Count == 1)) return new int[candidates.Count];
        if (!IsAvailable) return null;
        var request = $"E\t{Clean(context ?? "")}\t{Clean(reading)}\t{string.Join(RecordSeparator, candidates.Select(c => string.Join(UnitSeparator, c.Select(Clean))))}"
            + (string.IsNullOrEmpty(after) ? "" : "\t" + Clean(after));
        return Parse(Ask(request, live), candidates.Count);
    }

    /// <summary>
    /// 候補に縛られずに zenz に変換させた文字列と、その点数・選び直しの答え (baseline) の点数 (どちらも zenz の対数確率の合計)。
    /// zenz の出力は読みと合わないことがあるので、使う前に確かめる。準備中・間に合わない・出せないときは null。
    /// </summary>
    public (string Text, float Score, float BaselineScore)? Generate(string? context, string reading, string? after, string baseline)
    {
        if (!IsAvailable || reading.Length == 0 || baseline.Length == 0) return null;
        var request = $"G\t{Clean(context ?? "")}\t{Clean(reading)}\t{Clean(after ?? "")}\t{Clean(baseline)}";
        if (Ask(request, live: false) is not { Length: > 0 } line) return null;
        var fields = line.Split('\t');
        return fields.Length == 3
            && float.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score)
            && float.TryParse(fields[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var baselineScore)
            ? (fields[0], score, baselineScore)
            : null;
    }

    /// <summary>
    /// 1 行頼んで 1 行の返事を受け取る (同じ頼みは控えから返す)。準備中・間に合わない・失敗したときは null。
    /// live はライブ変換のとき (待つ時間を短くし、間に合わなければ返事が届いたときに <see cref="ResultReady"/> で知らせる)。
    /// </summary>
    private string? Ask(string request, bool live)
    {
        var options = _options();
        var budget = Math.Max(5, live ? options.LiveTimeoutMs : options.TimeoutMs);
        var started = Stopwatch.GetTimestamp();
        int Remaining() => Math.Max(0, budget - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        lock (_gate)
        {
            if (_disposed) return null;
            if (_results.TryGetValue(request, out var known))
            {
                _cacheHits++;
                return known;
            }
            if (Ready() is not { } process) return null;
            // 前の頼みの返事を待っている (ヘルパーは 1 つずつ計算する)。待つ時間のうちに来なければ、今回は Mozc の結果
            if (!CollectPending(Remaining()))
            {
                _busy++;
                // 前の返事が届いたら知らせてもらう (届いたあとに作り直せば、この読みを頼める)
                if (live) _wantsNotice = true;
                return null;
            }
            if (_results.TryGetValue(request, out known))
            {
                _cacheHits++;
                return known;
            }
            try
            {
                _requests++;
                var reply = process.StandardOutput.ReadLineAsync();
                process.StandardInput.Write(request + "\n");
                process.StandardInput.Flush();
                if (!reply.Wait(Remaining()))
                {
                    // 遅かった。返事はあとで控えに入れ、届いたら知らせる (表示を作り直すと、控えから結果が返る)
                    _late++;
                    _wantsNotice = live;
                    _pending = (request, reply);
                    reply.ContinueWith(_ => OnLateReply(reply), TaskScheduler.Default);
                    return null;
                }
                return Remember(request, reply.Result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn($"Zenzai を使えませんでした: {ex.Message}");
                Fail();
                return null;
            }
        }
    }

    /// <summary>起動しておく (重みの読み込みと GPU の準備に数秒かかるので、最初の変換の前に)。</summary>
    public void WarmUp()
    {
        if (!IsAvailable) return;
        lock (_gate) Ready();
    }

    /// <summary>起動して、準備ができるまで待つ (テストの道具用。アプリでは WarmUp を使い、待たない)。</summary>
    public bool WaitUntilReady(int timeoutMs)
    {
        Task? starting;
        lock (_gate)
        {
            if (Ready() is not null) return true;
            starting = _starting;
        }
        starting?.Wait(timeoutMs);
        lock (_gate) return _process is { HasExited: false };
    }

    /// <summary>選んだ結果の控えを捨てる (Mozc が覚えて候補の順番が変わったときなど)。</summary>
    public void ClearCache()
    {
        lock (_gate) _results.Clear();
    }

    /// <summary>待ちきれなかった返事が届いた: 控えに入れて知らせる (次の頼みがもう受け取っていれば何もしない)。</summary>
    private void OnLateReply(Task<string?> reply)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending is { } pending && pending.Reply == reply) CollectPending(0);
            // ライブ変換で間に合わなかった・前の返事を待っていて頼めなかった頼みがあれば知らせる
            // (次の頼みが先にこの返事を受け取っていても、知らせて作り直してもらう。作り直しは控えを使うので軽い)
            if (!_wantsNotice) return;
            _wantsNotice = false;
            _notified++;
        }
        try
        {
            ResultReady?.Invoke();
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Zenzai の結果を表示に反映できませんでした: {ex.Message}");
        }
    }

    /// <summary>待ちきれなかった返事が来ていれば控えに入れる (_gate の中で呼ぶ)。まだ来ていなければ false。</summary>
    private bool CollectPending(int waitMs)
    {
        if (_pending is not { } pending) return true;
        try
        {
            if (!pending.Reply.Wait(waitMs)) return false;
            Remember(pending.Key, pending.Reply.Result);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Zenzai の応答を読めませんでした: {ex.Message}");
            Fail();
            return false;
        }
        finally
        {
            if (pending.Reply.IsCompleted) _pending = null;
        }
        return true;
    }

    private static int[]? Parse(string? line, int count)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var fields = line.Split(UnitSeparator);
        if (fields.Length != count) return null;
        var picks = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (!int.TryParse(fields[i], out picks[i]) || picks[i] < 0) return null;
        }
        return picks;
    }

    /// <summary>返事 (の行) を控えに入れる (_gate の中で呼ぶ)。</summary>
    private string? Remember(string key, string? reply)
    {
        if (_results.Count > 512) _results.Clear();
        _results[key] = reply;
        return reply;
    }

    // タブ・改行・区切りの文字はやり取りに使うので空白にする
    private static string Clean(string text) =>
        text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Replace(UnitSeparator, ' ').Replace(RecordSeparator, ' ');

    /// <summary>
    /// 準備のできたヘルパー (_gate の中で呼ぶ)。動いていなければバックグラウンドで起動して null を返す。
    /// 使わない時間が続いてヘルパーが自分で終了したときも、ここで起動し直す。
    /// </summary>
    private Process? Ready()
    {
        var options = _options();
        if (_process is { HasExited: false } running && options == _startedWith) return running;
        if (_starting is { IsCompleted: false }) return null;
        if (_process is { } old)
        {
            if (options != _startedWith)
            {
                Diagnostics.Log.Info("Zenzai の設定が変わったので起動し直します。");
                Stop(old);
            }
            else if (old.HasExited && old.ExitCode != 0)
            {
                Diagnostics.Log.Warn($"Zenzai のヘルパーが終了していました (終了コード 0x{old.ExitCode:X8})。");
                _failures++;
            }
            old.Dispose();
            _process = null;
            _pending = null;
            _results.Clear();
        }
        if (!IsAvailable) return null;
        _starting = Task.Run(() => StartProcess(options));
        return null;
    }

    private void StartProcess(ZenzaiOptions options)
    {
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(_helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(_helperPath) ?? "",
            };
            foreach (var arg in new[] { "--model", options.ModelPath, "--backend", options.Backend, "--idle-exit", options.IdleExitMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                info.ArgumentList.Add(arg);
            }
            var previousMode = 0u;
            if (OperatingSystem.IsWindows())
            {
                previousMode = GetErrorMode();
                SetErrorMode(previousMode | SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
            }
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("起動できませんでした");
            }
            finally
            {
                if (OperatingSystem.IsWindows()) SetErrorMode(previousMode);
            }
            // llama.cpp のログ (標準エラー) は読み捨てる (溜まるとヘルパーが止まる)
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var ready = process.StandardOutput.ReadLineAsync();
            var line = ready.Wait(StartTimeoutMs) ? ready.Result : null;
            if (line is null || !line.StartsWith("READY", StringComparison.Ordinal))
            {
                if (process.WaitForExit(1000) && IsLoaderFailure(process.ExitCode)) _missingDependency = true;
                throw new InvalidOperationException(line ?? "応答なし");
            }
            lock (_gate)
            {
                if (_disposed)
                {
                    Stop(process);
                    return;
                }
                _process = process;
                _startedWith = options;
                _failures = 0;
            }
            Diagnostics.Log.Info($"Zenzai を起動しました ({line[5..].Trim()})。");
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Zenzai を起動できませんでした: {ex.Message}。Mozc の結果で変換します。");
            if (process is not null) Stop(process);
            lock (_gate) _failures++;
        }
    }

    private void Fail()
    {
        _failures++;
        if (_process is { } process) Stop(process);
        _process = null;
        _pending = null;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Write("Q\n");
                process.StandardInput.Flush();
                if (!process.WaitForExit(1000)) process.Kill();
            }
        }
        catch
        {
        }
        process.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_process is { } process) Stop(process);
            _process = null;
        }
    }
}
