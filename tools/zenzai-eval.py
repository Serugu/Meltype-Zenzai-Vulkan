# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Serugu
#
# Zenzai (zenz のヘルパー) の評価データを作る。1 行に「文脈<TAB>読み<TAB>Mozc の文節の候補<TAB>正解|正解…」。
# zenz のヘルパーの --bench にそのまま渡せる (正解数と遅延が出る)。
#
#   python tools/zenzai-eval.py <meltype_mozc_helper.exe> <出力フォルダー> [AJIMEE-Bench の evaluation_items.json]
#
# - tools/zenzai-cases.txt・tools/zenzai-holdout.txt から zenzai-cases.tsv・zenzai-holdout.tsv を作る
# - AJIMEE-Bench (https://github.com/azooKey/AJIMEE-Bench、JWTD_v2/v1/evaluation_items.json、CC-BY-SA 3.0) を渡すと、
#   ajimee-tune.tsv・ajimee-test.tsv (100 問ずつ) も作る。データはライセンスが違うので、このリポジトリには入れない
import json
import os
import subprocess
import sys
import tempfile

SENTENCE_ENDS = "。．！？\n\r"


def to_hiragana(text):
    return "".join(chr(ord(c) - 0x60) if "ァ" <= c <= "ヶ" else c for c in text)


def meltype_context(text, length=10):
    # CompositionController.ConversionContext と同じ: 最後の文の終わりより後ろの、末尾の 10 文字まで
    start = max(text.rfind(c) for c in SENTENCE_ENDS)
    return text[start + 1:].strip()[-length:]


def long_context(text):
    # CompositionController.SentenceBefore(40): zenz にだけ渡す長い左の文脈
    return meltype_context(text, 40)


def main():
    helper, outdir = sys.argv[1], sys.argv[2]
    ajimee = sys.argv[3] if len(sys.argv) > 3 else None
    tools = os.path.dirname(os.path.abspath(__file__))
    os.makedirs(outdir, exist_ok=True)
    mozc = subprocess.Popen([helper, tempfile.mkdtemp(prefix="meltype-zenzai-eval-")],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)

    def read_line():
        return mozc.stdout.readline().decode("utf-8").rstrip("\r\n")

    assert read_line() == "READY"

    def segments(context, reading):
        mozc.stdin.write(f"C\t{context}\t{reading}\n".encode())
        mozc.stdin.flush()
        result = []
        for record in read_line().split("\x1e"):
            fields = record.split("\x1f")
            if not fields[0]:
                continue
            candidates = []
            for candidate in fields[1:]:
                if candidate and candidate not in candidates:
                    candidates.append(candidate)
            result.append("\x1f".join(candidates or [fields[0]]))
        return "\x1e".join(result)

    def write(name, rows):
        # rows: (Mozc に渡す文脈, zenz に渡す左の文脈, 読み, 正解, zenz に渡す右の文脈)
        with open(os.path.join(outdir, name), "w", encoding="utf-8", newline="\n") as f:
            for mozc_context, zenz_context, reading, answers, right in rows:
                line = f"{zenz_context}\t{reading}\t{segments(mozc_context, reading)}\t{'|'.join(answers)}"
                f.write(line + (f"\t{right}" if right else "") + "\n")
        print(name, len(rows))

    for name in ("zenzai-cases", "zenzai-holdout"):
        rows = []
        with open(os.path.join(tools, name + ".txt"), encoding="utf-8") as f:
            for line in f:
                if " / " in line:
                    answer, reading = line.strip().split(" / ")
                    rows.append(("", "", reading, answer.split("|"), ""))
        write(name + ".tsv", rows)

    if ajimee:
        with open(ajimee, encoding="utf-8") as f:
            items = json.load(f)

        def right_of(x):
            # 変換する部分の後ろの文 (同じ文の中、40 文字まで)。original_text の中で、左の文脈と答えの後ろ
            original = x["original_text"]
            for answer in x["expected_output"]:
                at = original.find(x["context_text"] + answer)
                if at >= 0:
                    rest = original[at + len(x["context_text"]) + len(answer):]
                    end = min([rest.find(c) for c in SENTENCE_ENDS if c in rest] or [len(rest)])
                    return rest[:end][:40]
            return ""

        # Meltype と同じく、Mozc にも zenz にも左の文脈を 10 文字だけ渡す
        short = [(meltype_context(x["context_text"]), meltype_context(x["context_text"]), to_hiragana(x["input"]), x["expected_output"], "") for x in items]
        # Mozc には 10 文字、zenz には 40 文字
        long = [(meltype_context(x["context_text"]), long_context(x["context_text"]), to_hiragana(x["input"]), x["expected_output"], "") for x in items]
        # さらに zenz に右の文脈 (文の途中を打ち直すとき)
        right = [(m, z, r, a, right_of(x)) for (m, z, r, a, _), x in zip(long, items)]
        # 重みを決める用と確かめる用に半分ずつ (文脈あり・なしが半々になるよう、交互に分ける)
        for suffix, rows in (("", short), ("-ctx40", long), ("-ctx40-right", right)):
            write(f"ajimee-tune{suffix}.tsv", rows[0::2])
            write(f"ajimee-test{suffix}.tsv", rows[1::2])

    mozc.stdin.write(b"Q\n")
    mozc.stdin.flush()


if __name__ == "__main__":
    main()
