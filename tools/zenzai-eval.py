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


def meltype_context(text):
    # CompositionController.ConversionContext と同じ: 最後の文の終わりより後ろの、末尾の 10 文字まで
    start = max(text.rfind(c) for c in SENTENCE_ENDS)
    return text[start + 1:].strip()[-10:]


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
        with open(os.path.join(outdir, name), "w", encoding="utf-8", newline="\n") as f:
            for context, reading, answers in rows:
                f.write(f"{context}\t{reading}\t{segments(context, reading)}\t{'|'.join(answers)}\n")
        print(name, len(rows))

    for name in ("zenzai-cases", "zenzai-holdout"):
        rows = []
        with open(os.path.join(tools, name + ".txt"), encoding="utf-8") as f:
            for line in f:
                if " / " in line:
                    answer, reading = line.strip().split(" / ")
                    rows.append(("", reading, answer.split("|")))
        write(name + ".tsv", rows)

    if ajimee:
        with open(ajimee, encoding="utf-8") as f:
            items = json.load(f)
        rows = [(meltype_context(x["context_text"]), to_hiragana(x["input"]), x["expected_output"]) for x in items]
        # 重みを決める用と確かめる用に半分ずつ (文脈あり・なしが半々になるよう、交互に分ける)
        write("ajimee-tune.tsv", rows[0::2])
        write("ajimee-test.tsv", rows[1::2])

    mozc.stdin.write(b"Q\n")
    mozc.stdin.flush()


if __name__ == "__main__":
    main()
