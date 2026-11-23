# Windows 版と Web 版の結果が同じか確かめる

割付・コードの作り方（`geom.js` / `app.js`）を変えたら、Windows 版（`Geom.cs` / `Editor*.cs`）にも同じ変更を入れて、次の手順で同じ結果になるか確かめる。

1. 確かめるための図面を作る（holes、notches、L、blocked、overlap、slot、mj-rect、mj-rect-tall、decimal、no-tools）

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

v0.17.11 では、10 個の図面すべてでコード 449 行が一致した（F23 のピッチ・列数を書き換えた図面でも一致した）。
