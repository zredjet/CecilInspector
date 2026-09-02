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

`global.json`で.NET 10 SDKを固定し、`Directory.Build.props`で警告をエラー扱いにしたうえで.NETアナライザーとエディター設定の検証をビルド時に有効化しています。CI（GitHub Actions）はLinux、Windows、macOSでビルドとテストを実行します。

配布用の単一実行ファイルを作る場合（例: Windows x64）:

```bash
dotnet publish src/CecilInspector -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true
```

`v1.2.3`形式のタグをpushすると、GitHub Actionsの`Release`ワークフローがWindows x64向けの自己完結・単一ファイル版を発行し、`cecil-inspector-1.2.3-win-x64.zip`（実行ファイル、LICENSE、README入り）をGitHub Releaseに添付します。バージョン番号はタグから取り、`--version`の表示と一致します。

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
| `--kind` | `namespace,type,method,property,field,event,all`（カンマ区切り） | `all` |
| `--scope` | `definitions`, `references`, `all` | `definitions` |
| `--match` | `contains`, `exact`, `regex` | `contains` |
| `--case-sensitive` | 大文字・小文字を区別 | 区別しない |
| `--symbols` | `auto`, `off`, `required` | `auto` |
| `--max-results` | メモリに保持して表示する最大件数（総件数は全件集計） | `1000` |
| `--no-recursive` | サブフォルダを検索しない | 再帰検索 |
| `--output`, `-o` | 新規作成するUTF-8出力ファイル | なし |
| `--reference-path` | 依存アセンブリの検索フォルダ（複数回指定可） | 入力内のアセンブリ所在フォルダ |

入力がフォルダの場合は、`.dll`、`.exe`、`.netmodule`を対象にします。単一のmanifest DLLを指定した場合は、そのassemblyに含まれるsecondary netmoduleも検索します。依存DLLは「解析対象DLLの隣接フォルダ → `--reference-path`の指定順 → 入力内のアセンブリ所在フォルダ → フレームワークの既知フォルダ → 実行中ランタイムのアセンブリ → GAC（Windows）」の順で探索します。前半3つのユーザーが管理するフォルダでは、Version、Culture、PublicKeyTokenを含むAssemblyIdentityが完全一致する候補だけを採用します。フレームワーク由来の場所では、ランタイム自身のバインドと同じく、同じ名前・Culture・PublicKeyTokenで同じか新しいVersionの候補を採用します（`netstandard 2.0.0.0`が2.1.0.0のファサードへ解決されるなど）。フレームワークの既知フォルダとは、実行中のランタイム、`DOTNET_ROOT`や既定のインストール先にある.NETの共有ランタイム（`shared/Microsoft.NETCore.App/<version>`）と参照パック（`packs/Microsoft.NETCore.App.Ref`）、Windowsでは`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`などの.NET Frameworkフォルダと参照アセンブリです。単一ファイル配布版は自身のランタイムを内蔵しているためディスク上のフォルダが無く、この探索に依存します。たとえばnet8向けDLLは、.NET 8以降のランタイムか参照パックがインストールされていれば解決できます。入力外の依存DLLは`--reference-path`で追加してください。解決できない依存先があると、ファイルと依存先ごとに1行の警告（未解決メンバー数と例を含む）を標準エラーへ出し、終了コード`3`にします。特定バージョンの参照アセンブリで厳密に解決したい場合は、`--reference-path <dotnet>/packs/Microsoft.NETCore.App.Ref/8.0.x/ref/net8.0` のように参照アセンブリのフォルダを指定してください（このフォルダは完全一致で扱われます）。

オプションは位置引数の前後どちらにも置けます。`-`で始まる検索文言は`--`の後に指定します（例: `cecil-inspector search app.dll -- -Prefixed --match exact`）。

ネイティブDLL、壊れたファイル、アクセス不能なフォルダは標準エラーへ警告を出し、残りを続けます。フォルダ入力配下のファイル／ディレクトリのシンボリックリンクと再解析ポイントは追跡せず、警告と部分成功として扱います。入力ルート自体がディレクトリのシンボリックリンクの場合は拒否します。ハードリンクはパス単位で解析・集計します。

安全のため、`--output`は既存ファイルを上書きしません。また、レポート出力先に`.dll`、`.exe`、`.netmodule`は指定できません。同じフォルダの`.partial`一時ファイルへ書き、正常完了時だけ指定名へ確定します。一時パスがシンボリックリンク／再解析ポイントへ差し替えられた場合も確定を拒否します。出力先には、他ユーザーが書き換えられない信頼できるフォルダを指定してください。

### 終了コードと診断

| コード | 意味 |
|---:|---|
| `0` | 全対象を正常に解析 |
| `1` | CLI引数エラー、正規表現の照合タイムアウト |
| `2` | 入力エラー、全対象の解析失敗、出力失敗 |
| `3` | 一部対象だけ解析できた部分成功 |

検索結果とダンプ本体は標準出力および`--output`へ、警告は標準エラーへ出力します。自動化では終了コード3を「不完全な結果」として扱ってください。環境変数`CECIL_INSPECTOR_DEBUG=1`を設定すると、警告の原因となった例外のスタックトレースも標準エラーへ出力します。

### PDBと行番号

`--symbols auto`はPortable PDB、Windows PDBなど、Mono.Cecilが利用できるシンボルを自動的に読みます。外部PDBだけでなく埋め込みPortable PDBも対象です。外部PDBが壊れている場合はシンボルなしで再試行し、その旨を標準エラーへ警告します（終了コードには影響しません）。名前空間・型・フィールドの定義だけを検索する場合は、行番号を表示できないためPDB読込を省略します。

- メソッド定義: 最初の非hiddenシーケンスポイント
- async/iterator定義: 生成されたステートマシンの`MoveNext`にある最初の有効なシーケンスポイントへフォールバック
- プロパティ/イベント定義: getter/setterまたはadd/remove/raise/other semanticsメソッドの最初のシーケンスポイント
- IL内の参照: 該当IL命令以前で最も近い非hiddenシーケンスポイント
- 名前空間、型、フィールド定義: PDBに宣言位置がないため、通常は行番号なし

Releaseビルドの最適化や非同期/イテレーターのステートマシンにより、表示行がソース上の直感的な位置とずれる場合があります。PDBがない場合も検索自体は可能です。

## メタデータダンプ

```bash
cecil-inspector dump app.dll
cecil-inspector dump ./bin --include-il --output metadata.txt
```

アセンブリ参照、リソース、型、基底型、インターフェイス、フィールド、プロパティ、イベント、メソッド、パラメーターを出力します。`--include-il`を付けるとIL命令と利用可能なソース行も出力します。

検索レポートとダンプはコンソールと一時ファイルへ逐次書き込むため、レポート全文をメモリへ蓄積しません。完了後に一時ファイルを`--output`の指定名へ確定します。解析中のファイル差し替えによる部分ヒット混入を防ぐため、各対象ファイルは解析前に丸ごとメモリへ読み込み、そのコピーに対して必要な部分だけを遅延解析します。

## 検索上の注意

- `--match regex`は.NETの非バックトラッキングエンジンを優先し、照合時間が入力長に対して線形になります。先読み・後読み、後方参照、アトミックグループなど非対応の構文を含むパターンだけは従来のエンジンへフォールバックし、1回の照合に250ミリ秒のタイムアウトを適用します。
- `references`はIL命令のオペランドに現れる参照です。リフレクション、DI設定、文字列から解決される型名・メソッド名は検出できません。
- メソッド参照の宣言型、戻り型、引数型、ジェネリック実引数と、フィールド参照のフィールド型も型参照として検索します。
- プロパティとイベントの参照は、参照先を解決できる場合はMethodSemanticsと所有Property/Eventから分類します。解決不能な`get_`/`set_`、`add_`/`remove_`/`raise_`参照は、偽陰性を避けるため通常メソッドとアクセサー候補（プロパティ／イベント）の両方として扱います。
- プロパティ／イベント参照の分類に必要な依存アセンブリを解決できない場合は、結果が不完全であることを標準エラーへ警告し、終了コード`3`にします。
- 明示的インターフェイス実装は完全なメタデータ名を表示しますが、`Save`のような末尾の論理メンバー名でも完全一致検索できます。
- 型や名前空間の参照は、メンバー参照の宣言型も含むため、同じソース行で複数件になることがあります。
- シグネチャ表示はCecil形式を基にした正規形です。ネスト型は`+`、ジェネリック実引数は`, `区切り（例: ``System.Func`2<System.Int32, System.String>``）、ジェネリックパラメーターは型の`!n`／メソッドの`!!n`で表し、定義と参照のどちらのスコープでも同じ文字列になります。同じ完全型名が別アセンブリにあるため衝突する場合は`@Assembly`でスコープを付加します。
- 出力に表示された正規シグネチャは、そのまま`--match exact`の検索文言として再利用できます。

## ライセンス

[MIT License](LICENSE)。依存する[Mono.Cecil](https://github.com/jbevain/cecil)もMITライセンスです。
