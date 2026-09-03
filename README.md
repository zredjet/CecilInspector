# CecilInspector

Mono.Cecilを使い、.NET Frameworkおよび現行.NETのDLL/EXEをロードせずに検索するコンソールツールです。メタデータ上の定義と、メソッド本体（IL）に現れる参照を区別して検索できます。

## 主な用途

- 名前空間、型、メソッド、プロパティ、フィールド、イベントの定義を横断検索する
- 同名メソッドを宣言型と引数型を含むシグネチャで判別する
- ジェネリックarity、ジェネリック実引数、インデクサー引数を含めてオーバーロードを判別する
- メソッド呼び出しやプロパティ/フィールドアクセスの参照元を調べる
- PDBがある場合、該当するソースファイルと行番号を表示する
- 調査・デバッグ用にCecilが読んだメタデータとILをダンプする

解析対象のアセンブリは実行・ロードしません。このため、対象が.NET Framework製でも、ツール自体は現行.NET上で実行できます。

## ビルド

前提: .NET 10 SDK

```bash
dotnet restore CecilInspector.slnx
dotnet build CecilInspector.slnx -c Release --no-restore
dotnet test CecilInspector.slnx -c Release --no-build
```

実行例:

```bash
dotnet run --project src/CecilInspector -- search ./assemblies CustomerService --kind type,method
dotnet run --project src/CecilInspector -- --version
```

`global.json`で.NET 10 SDKを固定し、`Directory.Build.props`で警告をエラー扱いにしたうえで.NETアナライザーとエディター設定の検証をビルド時に有効化しています。命名規則とusingの並び順はビルドでは検証されないため、コミット前に`dotnet format CecilInspector.slnx --verify-no-changes`で確認してください。CI（GitHub Actions）はLinux、Windows、macOSでフォーマット検証、ビルド、テスト（カバレッジ収集付き）を実行し、自己完結・単一ファイル版を発行して簡易動作確認まで行います。

配布用の単一実行ファイルを作る場合（例: Windows x64）:

```bash
dotnet publish src/CecilInspector -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true
```

`v1.2.3`形式のタグをpushすると、GitHub Actionsの`Release`ワークフローがWindows x64向けの自己完結・単一ファイル版を発行し、`cecil-inspector-1.2.3-win-x64.zip`（実行ファイル、LICENSE、README入り）とそのSHA-256（`.zip.sha256`）をGitHub Releaseに添付します。タグは`src/CecilInspector/CecilInspector.csproj`の`<Version>`と一致している必要があり（不一致ならワークフローが失敗します）、`--version`の表示もこの値です。

## 検索

```text
cecil-inspector search <assembly-or-directory> <query> [options]
```

例:

```bash
# メソッド定義を部分一致で検索
cecil-inspector search ./bin Save --kind method

# Saveというメソッドの定義と呼び出し元を完全一致で検索
cecil-inspector search ./bin Save --kind method --scope all --match exact

# 型を限定し、Saveの全オーバーロードを完全一致で検索
cecil-inspector search ./bin 'MyApp.CustomerService::Save' --kind method --match exact

# プロパティ参照だけを検索し、結果をファイルにも保存
cecil-inspector search app.dll CustomerId --kind property --scope references \
  --output customer-id.txt

# 正規表現（既定では大文字・小文字を区別しない）
cecil-inspector search ./bin '^(Save|Update)$' --kind method --match regex
```

主要オプション:

| オプション | 値 | 既定値 |
|---|---|---|
| `--kind` | `namespace,type,method,property,field,event,all`（カンマ区切り、複数回指定時は和集合） | `all` |
| `--scope` | `definitions`, `references`, `all` | `definitions` |
| `--match` | `contains`, `exact`, `regex` | `contains` |
| `--format` | `text`, `msbuild`（エディターがジャンプできる`path(line,col):`形式。`--symbols off`とは併用不可） | `text` |
| `--case-sensitive` | 大文字・小文字を区別 | 区別しない |
| `--symbols` | `auto`, `off`, `required` | `auto` |
| `--max-results` | メモリに保持して表示する最大件数（総件数は全件集計） | `1000` |
| `--no-recursive` | サブフォルダを検索しない | 再帰検索 |
| `--output`, `-o` | 新規作成するUTF-8出力ファイル | なし |
| `--reference-path` | 依存アセンブリの検索フォルダ（複数回指定可） | 入力内のアセンブリ所在フォルダ |
| `--quiet`, `-q` | 標準エラーへの警告・情報の各行を出さない（件数の要約1行と終了コードは変わらない。`dump`でも使用可） | 出力する |

入力がフォルダの場合は、`.dll`、`.exe`、`.netmodule`を対象にします。単一のmanifest DLLを指定した場合は、そのassemblyに含まれるsecondary netmoduleも検索します。依存DLLは「解析対象DLLの隣接フォルダ → `--reference-path`の指定順 → 入力内のアセンブリ所在フォルダ → フレームワークの既知フォルダ → 実行中ランタイムのアセンブリ → GAC（Windows）」の順で探索します。前半3つのユーザーが管理するフォルダでは、Version、Culture、PublicKeyTokenを含むAssemblyIdentityが完全一致する候補だけを採用します。フレームワーク由来の場所では、ランタイム自身のバインドと同じく、同じ名前・Culture・PublicKeyTokenで同じか新しいVersionの候補を採用します（`netstandard 2.0.0.0`が2.1.0.0のファサードへ解決されるなど）。フレームワークの既知フォルダとは、実行中のランタイム、`DOTNET_ROOT`や既定のインストール先にある.NETの共有ランタイム（`shared/Microsoft.NETCore.App/<version>`）と参照パック（`packs/Microsoft.NETCore.App.Ref`）、Windowsでは`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`などの.NET Frameworkフォルダと参照アセンブリです。単一ファイル配布版は自身のランタイムを内蔵しているためディスク上のフォルダが無く、この探索に依存します。たとえばnet8向けDLLは、.NET 8以降のランタイムか参照パックがインストールされていれば解決できます。入力外の依存DLLは`--reference-path`で追加してください。フレームワーク由来の場所から解決したアセンブリは1回の実行で1度だけ開き、すべての入力ファイルで共有します。解決できない依存先があると、ファイルと依存先ごとに1行の警告（未解決メンバー数と例を含む）を標準エラーへ出し、終了コード`3`にします。この警告は`--scope references`/`all`でプロパティ・イベント参照を分類するために依存先を読む必要があるときに出るもので、典型的には入力フォルダに無いNuGet依存や、実行環境にインストールされていないフレームワーク（例: macOS/Linux上で.NET Framework製アプリを解析）が原因です。`CECIL_INSPECTOR_DEBUG=1`で「解決失敗:」の行から依存先を特定し、`--reference-path`で補えます。行数だけを抑えたい場合は`--quiet`を付けると各行の代わりに件数の要約1行だけになります（終了コードは`3`のままです）。特定バージョンの参照アセンブリで厳密に解決したい場合は、`--reference-path <dotnet>/packs/Microsoft.NETCore.App.Ref/8.0.x/ref/net8.0` のように参照アセンブリのフォルダを指定してください（このフォルダは完全一致で扱われます）。

オプションは位置引数の前後どちらにも置けます。値は`--kind method`と`--kind=method`のどちらの形式でも指定でき、`--reference-path`以外の値付きオプションを2回指定するとエラーになります（`--kind`だけは和集合になります）。`-`で始まる検索文言は`--`の後に指定します（例: `cecil-inspector search app.dll -- -Prefixed --match exact`）。`--`以降は、上表のオプション名そのものを除いて`-`で始まる文言も位置引数として扱うため、`--`の後にオプションを続けて書けます。`--help`／`-h`で使用方法、`--version`／`-v`でバージョンを表示します。

ネイティブDLL、壊れたファイル、アクセス不能なフォルダは標準エラーへ警告を出し、残りを続けます。解析の途中で失敗したファイルについては、そこまでに見つかったヒットを結果に含めたうえで警告を出し、終了コード`3`で結果が不完全であることを示します。フォルダ入力配下のファイル／ディレクトリのシンボリックリンクと再解析ポイントは追跡せず、警告と部分成功として扱います。入力ルート自体がディレクトリのシンボリックリンクの場合は拒否します。ハードリンクはパス単位で解析・集計します。

安全のため、`--output`は既存ファイルを上書きしません。また、レポート出力先に`.dll`、`.exe`、`.netmodule`は指定できません。同じフォルダの`.<名前>.<ランダム>.partial`一時ファイルへ書き、正常完了時だけ指定名へ確定します。Ctrl-C（SIGINT）、SIGTERM、SIGHUPで中断した場合は一時ファイルを削除して終了コード`130`で終了します（2回目の割り込みは即座に強制終了します）。一時パスがシンボリックリンク／再解析ポイントへ差し替えられた場合も確定を拒否します。出力先には、他ユーザーが書き換えられない信頼できるフォルダを指定してください。

### 終了コードと診断

| コード | 意味 |
|---:|---|
| `0` | 全対象を正常に解析 |
| `1` | CLI引数エラー、正規表現の照合タイムアウト |
| `2` | 入力エラー、全対象の解析失敗、出力失敗 |
| `3` | 一部対象だけ解析できた部分成功 |
| `130` | Ctrl-C／SIGTERMなどによる中断（`--output`の一時ファイルは削除済み） |

検索結果とダンプ本体は標準出力および`--output`へ、診断は標準エラーへ出力します。結果を不完全にした問題（終了コードに影響）は`警告:`、終了コードに影響しない注意（壊れたPDBをスキップしたなど）は`情報:`の接頭辞で出力するので、自動化では接頭辞で区別できます。自動化では終了コード3を「不完全な結果」として扱ってください。環境変数`CECIL_INSPECTOR_DEBUG=1`を設定すると、警告の原因となった例外のスタックトレースと依存アセンブリ解決の追跡も標準エラーへ出力します。

### PDBと行番号

`--symbols auto`はPortable PDB、Windows PDBなど、Mono.Cecilが利用できるシンボルを自動的に読みます。外部PDBだけでなく埋め込みPortable PDBも対象です。外部PDBが壊れている場合はシンボルなしで再試行し、その旨を`情報:`として標準エラーへ出力します（終了コードには影響しません）。名前空間・型・フィールドの定義だけを検索する場合は、行番号を表示できないためPDB読込を省略し、要約行に`symbols not read`と表示します。

- メソッド定義: 最初の非hiddenシーケンスポイント
- async/iterator定義: 生成されたステートマシンの`MoveNext`にある最初の有効なシーケンスポイントへフォールバック
- プロパティ/イベント定義: getter/setterまたはadd/remove/raise/other semanticsメソッドの最初のシーケンスポイント
- IL内の参照: 該当IL命令以前で最も近い非hiddenシーケンスポイント
- 名前空間、型、フィールド定義: PDBに宣言位置がないため、通常は行番号なし

Releaseビルドの最適化や非同期/イテレーターのステートマシンにより、表示行がソース上の直感的な位置とずれる場合があります。PDBがない場合も検索自体は可能です。

### エディター連携

検索結果の`source:`行はPDBのシーケンスポイントから得た`パス:行:列`です。ソースがそのパスに存在すれば、次の方法で該当行を直接開けます。

**VS Codeの統合ターミナル**: 既定の出力のままで、`source:`行のパスをCtrl（macOSはCmd）+クリックすると該当行が開きます。空白を含むパスはターミナルのリンク検出に掛からないことがあります。

**Visual Studioの外部ツール**: `--format msbuild`を付けると、ヒットを1行ずつ`パス(行,列): info CI0001: [definition/method] シンボル`の形式で出力します（この形式では検索文言を含む`Query:`行を出力しません）。**ツール > 外部ツール**に次のように登録し、「出力ウィンドウを使用」（Use Output window）を有効にすると、出力ウィンドウの行をダブルクリック、または「次のメッセージへ移動」で該当行にジャンプできます。

| 項目 | 値 |
|---|---|
| コマンド | `C:\tools\cecil-inspector.exe` |
| 引数 | `search "$(SolutionDir)" "$(CurText)" --scope all --format msbuild` |
| 初期ディレクトリ | `$(SolutionDir)` |
| オプション | 出力ウィンドウを使用、（必要なら）引数の入力を求める |

エディターで識別子を選択してからツールを実行すると、その名前を検索します。`CI0001`は定義、`CI0002`は参照で、参照は`(in 呼び出し元) @ IL_xxxx`を末尾に付けます。位置が取れないヒット（名前空間や型の定義など）は`パス(行):`接頭辞なしで出力し、誤ってDLLを開こうとしないようにしています。severityを`info`にしているのは、MSBuildの`<Exec>`から実行しても本物の警告として扱われないようにするためです。

**VS Codeのタスク**: 組み込みの問題マッチャー`$msCompile`が同じ形式を解釈するので、`tasks.json`に次のように登録すると結果が「問題」パネルに一覧表示され、クリックで該当行に飛べます。

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "cecil-inspector: search",
      "type": "shell",
      "command": "cecil-inspector",
      "args": ["search", "${workspaceFolder}/bin", "${input:query}", "--scope", "all", "--format", "msbuild"],
      "problemMatcher": "$msCompile"
    }
  ],
  "inputs": [{ "id": "query", "type": "promptString", "description": "検索文言" }]
}
```

PDBが無い、または記録されたソースパスが手元と異なる場合はリンクになりません。

## メタデータダンプ

```bash
cecil-inspector dump app.dll
cecil-inspector dump ./bin --include-il --output metadata.txt
```

アセンブリ参照、リソース、型、基底型、インターフェイス、フィールド、プロパティ、イベント、メソッド、パラメーターを出力します。`--include-il`を付けるとIL命令と利用可能なソース行も出力します。

検索レポートとダンプはコンソールと一時ファイルへ逐次書き込むため、レポート全文をメモリへ蓄積しません。完了後に一時ファイルを`--output`の指定名へ確定します。解析中のファイル差し替えによる部分ヒット混入を防ぐため、各対象ファイルは解析前に丸ごとメモリへ読み込み、そのコピーに対して必要な部分だけを遅延解析します。

## 検索上の注意

- `--match regex`は.NETの非バックトラッキングエンジンを優先し、照合時間が入力長に対して線形になります。先読み・後読み、後方参照、アトミックグループなど非対応の構文を含むパターンだけは従来のエンジンへフォールバックし、1回の照合に250ミリ秒のタイムアウトを適用します。
- `references`はIL命令のオペランドに現れる参照に加え、`catch`句の例外型（ハンドラー先頭のIL位置で報告）とローカル変数の型（メソッド先頭の`IL_0000`で報告）です。リフレクション、DI設定、文字列から解決される型名・メソッド名は検出できません。
- メソッド参照の宣言型、戻り型、引数型、ジェネリック実引数と、フィールド参照のフィールド型も型参照として検索します。
- プロパティとイベントの参照は、参照先を解決できる場合はMethodSemanticsと所有Property/Eventから分類します。解決不能な`get_`/`set_`、`add_`/`remove_`/`raise_`参照は、偽陰性を避けるため通常メソッドとアクセサー候補（プロパティ／イベント）の両方として扱います。
- プロパティ／イベント参照の分類に必要な依存アセンブリを解決できない場合は、結果が不完全であることを標準エラーへ警告し、終了コード`3`にします。
- 明示的インターフェイス実装は完全なメタデータ名を表示しますが、`Save`のような末尾の論理メンバー名でも完全一致検索できます。参照先の依存アセンブリが解決できない場合も同じです。
- ジェネリック型は`Cache`1`のようにarity付きで表示しますが、`Cache`や`MyApp.Cache`のようにarity無しでも完全一致検索できます（メソッドの`Save<T>`を`Save`で検索できるのと同じ）。閉じたジェネリック参照は`Cache`1<System.Int32>`とその定義`Cache`1`の両方が候補になり、arity無しの名前は定義側に一致します。
- 配列の疑似メソッド（`Get`/`Set`/`Address`/`.ctor`）は参照として表示しますが、定義が存在しないため未解決の依存先とは扱いません。
- 解決できない`raise_`アクセサー参照は、引数からイベント型を推定できないため型なしの`T::Changed`として表示します。
- 型や名前空間の参照は、メンバー参照の宣言型も含むため、同じソース行で複数件になることがあります。
- シグネチャ表示はCecil形式を基にした正規形です。ネスト型は`+`（`Outer+Inner::Save`。C#の`Outer.Inner`表記では一致しません）、ジェネリック実引数は`, `区切り（例: ``System.Func`2<System.Int32, System.String>``）、ジェネリックパラメーターは型の`!n`／メソッドの`!!n`で表し、定義と参照のどちらのスコープでも同じ文字列になります。同じ完全型名が別アセンブリにあるため衝突する場合は`@Assembly`でスコープを付加します。
- 出力に表示された正規シグネチャは、そのまま`--match exact`の検索文言として再利用できます。

## ライセンス

[MIT License](LICENSE)。依存する[Mono.Cecil](https://github.com/jbevain/cecil)もMITライセンスです。
