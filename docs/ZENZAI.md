# Zenzai (ニューラル変換) — zenzai ブランチ

Windows 版の Meltype に、azooKey の「Zenzai」で使われているかな漢字変換モデル **zenz** を足したものです。
Mozc が出した文節ごとの候補から、zenz が文全体と前の文の流れに合う組み合わせを選び直します。

```
感じ変換の制度を上げる   →  漢字変換の精度を上げる
機能の会議で話した内容   →  昨日の会議で話した内容
医師が転がる             →  石が転がる
```

## 仕組み

- `src/Meltype.Zenz/` … zenz のヘルパー。LLamaSharp (llama.cpp) で zenz-v3.2-small を動かす。Meltype から別のプロセスで起動し、標準入出力で 1 行ずつやり取りする (Mozc のヘルパーと同じ形)
- `src/Meltype.Core/Composition/ZenzaiReranker.cs` … ヘルパーの起動・やり取り。起動中・応答が遅い・失敗したときは null を返し、Mozc の結果を使う
- `src/Meltype.Core/Composition/HybridConverter.cs` … Zenzai が有効なら、Mozc の文節の区切りはそのままで、各文節の候補を zenz に選ばせる

打つのを待たせないために:

- ライブ変換 (打つたび) では 25ms だけ待つ。間に合わなければ Mozc の結果を見せ、zenz の結果が届いたら変換ボックスの表示を作り直す (`ZenzaiReranker.ResultReady` → `CompositionController.RefreshLiveConversion`)。Meltype IME はキーごとに表示を返すので、次のキーで反映される
- Space で変換したときは、設定「Zenzai を待つ時間」(既定 80ms) まで待つ。変換中・候補を選んでいる間は表示を差し替えない
- 文脈あり・なしの両方で変換して区切りを比べるところ (`ConvertWithContext`) は、先に Mozc の区切りだけを比べて、zenz には 1 回だけ頼む
- ヘルパーは前の頼みのプロンプトの KV キャッシュを残し、読みが伸びただけのプロンプトでは差分だけ計算する

選び方 (`src/Meltype.Zenz/Scorer.cs`):

- プロンプトは zenz-v3 の形 ` 左の文脈  読み (カタカナ) `。左の文脈は末尾の 40 文字まで
- 文節ごとにビームサーチ (幅 2、1 文節の候補は Mozc の上位 8 個まで)。点数は「出力のトークンの対数確率の合計 − Mozc の順位 × 0.75」で、最後に文の終わりの確率を足す
- Mozc の 1 番目が漢字なのに、かなだけの候補を選ぶときは 1 を引く (zenz はかな書きを好みがちで、測るとかなに崩す誤りが多かった)
- 値は AJIMEE-Bench の半分 (100 問) と `tools/zenzai-cases.txt` で決めた。ビーム幅 4 と 2 で正解数はほぼ同じで、2 のほうが 2〜3 割速い
- 送り仮名を落とした候補 (はこぶ → 運) は zenz に渡さない (`HybridConverter.Plausible`)
- ユーザーが前に選び直した語 (conversions.json) は、1 番目の候補にして zenz に渡す (`HybridConverter.PreferLearned`)。ほかの文節もその語に合わせて選ばれる
- プロンプトの KV キャッシュはすべての仮説で共有し、1 つの文節の仮説はまとめて 1 回のバッチで計算する

zenz の pre-tokenizer「gpt2-small-japanese-char」は本家の llama.cpp が知らない名前なので、読み込むときに `tokenizer.ggml.pre` を `gpt-2` に差し替えています (azooKey 版の llama.cpp も同じ扱い)。

## GPU とメモリ

既定は **GPU (Vulkan)** です。RTX 5070 Ti で測った値:

| | 1 回の選び直し: ライブ変換 (p50 / p95) | 長い文 (AJIMEE、p50 / p95) | VRAM | RAM (作業セット / private) |
| --- | --- | --- | --- | --- |
| Vulkan | 約 9ms / 18ms | 約 28ms / 83ms | 約 +140MB | 準備のあと 約 15MB / 約 240MB |
| CPU | — | 約 60ms / 82ms (自前の 40 文) | — | 約 110MB / 約 100MB |

ヘルパーの配布サイズは約 78MB (自己完結の .NET をトリミングし、llama.cpp の CPU 版は AVX2 と AVX なしだけ残す。最初は約 150MB)。

メモリを抑えるためにしていること:

- 重みは Q5_K_M (74MB) を mmap で読む (private のメモリにしない)
- KV キャッシュは 8bit (q8_0、flash attention)
- 準備のあとと、5 分使わなかったときに、物理メモリを手放す (EmptyWorkingSet。約 30MB → 約 7MB)。手放したあとの 1 回目は約 80ms かかる (ふだんは約 30ms)
- 10 分 (設定で変えられる) 使わなかったら、ヘルパーが自分で終了して VRAM も空ける。次に打ったときに 1 秒ほどで起動し直す (そのあいだは Mozc の結果)
- ヘルパーは文化圏のデータ (ICU) を読まず、GC の裏のスレッドも作らない

CUDA 版の LLamaSharp は `ggml-cuda.dll` だけで約 540MB あり、さらに CUDA 12 のランタイム (cuBLAS など) が要ります。74MB のモデルには割に合わないので使っていません。

## 精度

[AJIMEE-Bench](https://github.com/azooKey/AJIMEE-Bench) (azooKey の変換の評価データ。日本語 Wikipedia 入力誤りデータセットから作った 200 問、半分は左の文脈つき、CC-BY-SA 3.0) で、
Mozc の文節の候補から zenz が選んだ結果 (許される答えのどれかと同じか):

| | Mozc だけ | Mozc + Zenzai |
| --- | --- | --- |
| AJIMEE 重みを決める用 (100 問) | 53 | 76 |
| AJIMEE 確かめる用 (100 問) | 50 | 75 |

`tools/zenzai-cases.txt` (同音異義語の多い 80 文。重みを決めるのにも使った) と `tools/zenzai-holdout.txt` (使っていない 40 文) で、
打ったキーから通しで確かめた結果 (Space の最初の変換が出てほしい文と同じか):

| | Mozc だけ | Mozc + Zenzai |
| --- | --- | --- |
| zenzai-cases (80 文) | 57 | 66 (悪くなった文は 0) |
| zenzai-holdout (40 文) | 33 | 33 (1 文直り、1 文は「十分」が「10分」になった) |

```powershell
# 評価データ (AJIMEE-Bench はライセンスが違うので、取ってきて dist\ に置く。リポジトリには入れない)
gh api -H "Accept: application/vnd.github.raw" repos/azooKey/AJIMEE-Bench/contents/JWTD_v2/v1/evaluation_items.json > dist\zenzai-eval\ajimee.json
python tools\zenzai-eval.py native\mozc\bin\meltype_mozc_helper.exe dist\zenzai-eval dist\zenzai-eval\ajimee.json
powershell -ExecutionPolicy Bypass -File .\tools\Zenzai-Sweep.ps1 -Configs '', '--beam 4', '--rank-weight 1'   # 重みを変えて正解数を比べる

# 打ったキーから通しで (Zenzai なし / あり)
$env:MELTYPE_MOZC = "$PWD\native\mozc\bin\meltype_mozc_helper.exe"
$env:MELTYPE_ZENZ = "$PWD\dist\zenzai\zenz\Meltype.Zenz.exe"   # 空にすると Mozc だけ
Get-Content tools\zenzai-cases.txt | dotnet run -c Release --project src/Meltype.Core.Tests -- --jht-batch
# 起動・速さ・落ちたときの戻り・ライブ変換の差し替え
dotnet run -c Release --project src/Meltype.Core.Tests -- --zenzai-check $env:MELTYPE_ZENZ
```

## 入れ方

公式の Meltype を入れてから、このブランチで:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Install-Zenzai.ps1 -Enable
```

- `Meltype.dll`・`Meltype.Core.dll` を入れ替え、`zenz\` にヘルパーを置き (前の版の `zenz\` はごみ箱へ送ります)、重みが無ければ取ってきます (管理者権限は要りません)
- 版は `1.2.1-zenzai` になり、公式の自動更新では上書きされません
- 戻すときは `-Restore` (公式の DLL に戻し、`zenz\` をごみ箱へ送ります)

設定 (トレイの「設定」→「1. 全般」): 「Zenzai (ニューラル変換)」「Zenzai の計算に使う装置」「Zenzai を休ませるまでの時間 (分)」「Zenzai を待つ時間 (ms)」。

## 分かっている制限

- 文節の区切りは Mozc のまま。zenz が選べるのは Mozc の候補の中からだけ (庭には二羽鶏がいる のように、区切りから違うと直せない)
- azooKey の Zenzai がする「候補に無い変換を zenz が出し、辞書で確かめ直す」動きは入れていない
- 長い文 (20 文字を超えるような読み) は 1 回の選び直しに 30〜100ms かかり、ライブ変換では Mozc の結果が一瞬見えてから差し替わることがある
- インストールしてすぐ・GPU のドライバーを更新したあとは、最初の数回だけシェーダーの用意で遅く、Mozc の結果になることがある
