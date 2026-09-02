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
```

配布用の単一実行ファイルを作る場合（例: Windows x64）:

```bash
dotnet publish src/CecilInspector -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true
```

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

入力がフォルダの場合は、`.dll`、`.exe`、`.netmodule`を対象にします。単一のmanifest DLLを指定した場合は、そのassemblyに含まれるsecondary netmoduleも検索します。依存DLLは「解析対象DLLの隣接フォルダ → `--reference-path`の指定順 → 入力内のアセンブリ所在フォルダ」の順で探索し、Version、Culture、PublicKeyTokenを含むAssemblyIdentityが一致する候補だけを採用します。入力外の依存DLLは`--reference-path`で追加してください。

ネイティブDLL、壊れたファイル、アクセス不能なフォルダは標準エラーへ警告を出し、残りを続けます。フォルダ入力配下のファイル／ディレクトリのシンボリックリンクと再解析ポイントは追跡せず、警告と部分成功として扱います。入力ルート自体がディレクトリのシンボリックリンクの場合は拒否します。ハードリンクはパス単位で解析・集計します。

安全のため、`--output`は既存ファイルを上書きしません。また、レポート出力先に`.dll`、`.exe`、`.netmodule`は指定できません。同じフォルダの`.partial`一時ファイルへ書き、正常完了時だけ指定名へ確定します。一時パスがシンボリックリンク／再解析ポイントへ差し替えられた場合も確定を拒否します。出力先には、他ユーザーが書き換えられない信頼できるフォルダを指定してください。

### 終了コードと診断

| コード | 意味 |
|---:|---|
| `0` | 全対象を正常に解析 |
| `1` | CLI引数エラー、正規表現の照合タイムアウト |
| `2` | 入力エラー、全対象の解析失敗、出力失敗 |
| `3` | 一部対象だけ解析できた部分成功 |

検索結果とダンプ本体は標準出力および`--output`へ、警告は標準エラーへ出力します。自動化では終了コード3を「不完全な結果」として扱ってください。

### PDBと行番号

`--symbols auto`はPortable PDB、Windows PDBなど、Mono.Cecilが利用できるシンボルを自動的に読みます。外部PDBだけでなく埋め込みPortable PDBも対象です。外部PDBが壊れている場合はシンボルなしで再試行します。名前空間・型・フィールドの定義だけを検索する場合は、行番号を表示できないためPDB読込を省略します。

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

検索レポートとダンプはコンソールと一時ファイルへ逐次書き込むため、レポート全文をメモリへ蓄積しません。完了後に一時ファイルを`--output`の指定名へ確定します。解析中のファイル差し替えによる部分ヒット混入を防ぐため、各対象moduleのメタデータはファイル単位でメモリへ即時読み込みします。

## 検索上の注意

- `references`はIL命令のオペランドに現れる参照です。リフレクション、DI設定、文字列から解決される型名・メソッド名は検出できません。
- メソッド参照の宣言型、戻り型、引数型、ジェネリック実引数と、フィールド参照のフィールド型も型参照として検索します。
- プロパティとイベントの参照は、参照先を解決できる場合はMethodSemanticsと所有Property/Eventから分類します。解決不能な`get_`/`set_`、`add_`/`remove_`参照は、偽陰性を避けるため通常メソッドとアクセサー候補の両方として扱います。
- プロパティ／イベント参照の分類に必要な依存アセンブリを解決できない場合は、結果が不完全であることを標準エラーへ警告し、終了コード`3`にします。
- 明示的インターフェイス実装は完全なメタデータ名を表示しますが、`Save`のような末尾の論理メンバー名でも完全一致検索できます。
- 型や名前空間の参照は、メンバー参照の宣言型も含むため、同じソース行で複数件になることがあります。
- シグネチャ表示はCecil形式です（ネスト型は読みやすさのため`+`表記）。同じ完全型名が別アセンブリにあるため衝突する場合は`@Assembly`でスコープを付加し、ジェネリックパラメーターは`!n`/`!!n`で区別します。
- 出力に表示された正規シグネチャは、そのまま`--match exact`の検索文言として再利用できます。
