# Windows 版と Web 版の結果が同じか確かめる

割付・コードの作り方（`geom.js` / `app.js`）を変えたら、Windows 版（`Geom.cs` / `Editor*.cs`）にも同じ変更を入れて、次の手順で同じ結果になるか確かめる。

1. 確かめるための図面を作る（holes、notches、L、blocked、overlap、slot、mj-rect、mj-rect-tall、decimal、no-tools、circles、nibble、nibble-thick、round-outer、round-outer-2）

   ```
   python windows/tests/make_cases.py
   ```

   `windows/build/cases.json` ができる（Web 版の「ファイルに書き出す」と同じ形）。

2. Windows 版で、すべての図面のすべてのワークを自動割付して、コードを書き出す（開発用の隠しコマンド）

   ```
   dotnet build windows/SetPressCam -c Debug -o windows/build/debug
   windows/build/debug/SetPressCam.exe --gencode windows/build/cases.json windows/build/cs_out.txt
   ```

   `cs_out.txt` にコード、`cs_out.txt.json` に割り付けたあとの図面が入る。

3. Web 版（`python -m http.server 8765 --bind 127.0.0.1` で開いたページ）の開発者ツールで、同じ図面を `loadDoc` で読み、ワークごとに自動割付して `genCode` の結果を `cs_out.txt` と比べる。
   `cs_out.txt.json` の図面も Web 版で開き、割付（`hits`）と警告が同じで、図面を書き出し直しても同じ JSON になることを確かめる。

4. コードの読み取り（「編集」→「反映」）を変えたときは、書き換えたコードを `windows/build/edits.json`（`[{"name": 図面の名前, "code": 書き換えたコード}]`）に並べ、Windows 版で反映した結果を書き出す

   ```
   windows/build/debug/SetPressCam.exe --applycode windows/build/cases.json windows/build/edits.json windows/build/cs_apply.txt
   ```

   書き換えごとに `## 番号 図面` のあと、反映したら `APPLIED` と作り直したコードと図面の JSON、変わらなければ `SAME`、誤りがあれば「行: 内容」が並ぶ。
   Web 版でも同じ図面を `loadDoc` で読み、ワークごとに `autoLayout` してから `applyCode` を呼び、同じ形の文を作って比べる（図面の JSON は、Web 版が `tool: null` を書くなどキーの並びと null の扱いが違うので、null のキーを除きキーを並べ替えてから比べる）。

v0.17.11 では、10 個の図面すべてでコード 449 行が一致した（F23 のピッチ・列数を書き換えた図面でも一致した）。
v0.17.12 では、F3・F13・単発・T・F23・プログラム番号を書き換えたもの、誤りのあるもの、ワークを消したものなど 116 通りの書き換えで、反映の結果（作り直したコード・割付・寸法・誤りの一覧）がすべて一致した。
v0.18.0 では、丸穴の図面（circles）を加えた 11 個の図面でコード 515 行が一致し、丸穴の単発を書き換えるなど 6 通りの書き換えの反映もすべて一致した。
v0.19.0 では、丸の追い抜き（F5）の図面（nibble、nibble-thick）を加えた 13 個の図面でコード 618 行と割り付けた図面が一致し、F5 のピッチ・直径・金型直径・板厚を書き換えたものや誤りのあるものなど 16 通りの書き換えの反映もすべて一致した。
v0.20.0 では、丸の外形の図面（round-outer、round-outer-2）を加えた 15 個の図面でコード 780 行と割り付けた図面が一致し、すべての図面のコードをそのまま反映すると SAME、丸の外形の F5 のピッチを書き換えたものとピッチが小さすぎるものの反映も一致した。
