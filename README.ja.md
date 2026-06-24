# DroneShow — RUST ドローン制御プラグイン

**言語 / Language:** [English](README.md) | **日本語**

複数のドローンを制御して **編隊飛行・ライト・文字/パターン表示・ドローンショー**、および **ウェーブ型ミニゲーム（突撃・爆撃・射撃・ボス）** を実現する Oxide/Carbon プラグインです。**単体で動作**し、外部プラグインへの依存はありません。

> **多言語対応**: チャットメッセージは uMod のローカリゼーション API に対応しており、**既定は英語**、プレイヤーの言語設定が日本語なら**日本語**で表示されます。翻訳は `oxide/lang/`（Carbon は `carbon/lang/`）の `en` / `ja` フォルダに自動生成され、自由に追加・編集できます。

---

## 目次
- [導入](#導入)
- [編隊・ショー コマンド](#編隊ショー-コマンド)
- [文字・パターン表示](#文字パターン表示)
- [向きと自動演出](#向きと自動演出)
- [パターン作成ツール（UIエディタ）](#パターン作成ツールuiエディタ)
- [ウェーブ型ミニゲーム](#ウェーブ型ミニゲーム)
- [設定（config）](#設定config)
- [多言語対応（ローカリゼーション）](#多言語対応ローカリゼーション)
- [仕組み（技術メモ）](#仕組み技術メモ)
- [テスト手順・注意点](#テスト手順注意点)

---

## 導入

1. `DroneShow.cs` をサーバーの `carbon/plugins/`（Oxide の場合 `oxide/plugins/`）に置く。
2. 自動コンパイル後、設定ファイルとデータファイルが生成される。
3. 権限を付与:
   ```
   oxide.grant user <SteamID> droneshow.use     # 編隊・文字・ショー・パターン編集
   oxide.grant user <SteamID> droneshow.admin   # ミニゲーム管理
   ```
   （管理者(`IsAdmin`)は権限なしでも全コマンド可）

---

## 編隊・ショー コマンド
権限: `droneshow.use`

| コマンド | 説明 |
|---|---|
| `/drone spawn <グループ> <数> [高さ]` | 視線の先にグループを生成（高さ省略時は既定値） |
| `/drone formation <グループ> <line\|grid\|circle\|sphere> [間隔]` | 編隊を変更 |
| `/drone move <グループ> here` / `... <x> <y> <z>` | 編隊中心を移動 |
| `/drone rotate <グループ> <角度>` | 編隊の向き(Yaw, 度) |
| `/drone scale <グループ> <倍率>` | 編隊の拡大縮小 |
| `/drone light <グループ> <on\|off>` | ライトの ON/OFF |
| `/drone show <グループ> <on\|off>` | 自動ショー（編隊巡回＋回転＋ライト） |
| `/drone prefabs [キーワード]` | スポーン可能なプレハブを検索（既定 `drone`） |
| `/drone list` | グループ一覧 |
| `/drone clear <グループ\|all>` | 撤去 |

---

## 文字・パターン表示

ドット（1ドット＝1機）でメッセージや任意の絵柄を表現します。**グループが無ければ自動で作成し、必要機数を自動でスポーン**します（事前の `/drone spawn` 不要）。余剰機は地中へ退避し**消灯**されて隠れます。

| コマンド | 説明 |
|---|---|
| `/drone text <グループ> <文字>` | 内蔵フォントで文字を表示（A-Z 0-9 一部記号） |
| `/drone pattern <グループ> <保存名>` | **保存済みパターンを表示**（UIエディタで作成・保存したもの） |
| `/drone pattern <グループ> <行1> <行2> ...` | 配列を直接入力して表示（各行 `#`/`.` または `1`/`0`） |
| `/drone sequence <グループ> <切替秒> <項目1>\|<項目2>...` | 文字・形・保存パターンを一定間隔で切替表示 |

> **絵柄（パターン）は、ゲーム内の UI エディタ `/dronepattern` で作るのがおすすめ**です。マウスでセルをクリックして描き、名前を付けて保存 → `/drone pattern <グループ> <名前>` で呼び出します（[パターン作成ツール](#パターン作成ツールuiエディタ) 参照）。チャットに配列を打ち込む方法は、簡単な絵柄向けの簡易手段です。

**例:**
```
/drone text show1 RUST
/dronepattern new heart      # UIエディタでハートを描いて保存
/drone pattern show1 heart       # 保存した「heart」を表示
/drone pattern show1 .###. #...# ##### #...# #...#   # 配列を直接入力（「A」）
/drone sequence show1 5 flat HELLO | circle | pattern heart | up WORLD | sphere
```

- **sequence の項目**は、`|`（パイプ）区切りで以下の3種類を自由に並べられます:
  - **文字**（例 `HELLO`）… 内蔵フォントで表示。
  - **組み込みの形**（編隊）… 次のいずれか:
    | 名前 | 形 |
    |---|---|
    | `line` | 横一列に並ぶ |
    | `grid` | 格子状（正方形に近い面）に並ぶ |
    | `circle` | 円（リング）状に並ぶ |
    | `sphere` | 球状に立体配置（フィボナッチ球で均等配置） |
  - **保存パターン**… `pattern <保存名>`（または `pat <保存名>`）。UIエディタ `/dronepattern` で作って保存した絵柄を呼び出します。指定名が無い場合はエラーになります。
- 文字・パターン項目の頭に **`flat`**（水平＝下から見れる）/ **`up`**（横向き）を付けると、その項目だけ向きを指定できます（形には影響しません）。
- 大きく形が変わる切替は **4〜6秒** 推奨（短すぎると整列前に切り替わって崩れて見えます）。
- 必要機数は全項目の最大値に自動で合わせます（最も点数の多い項目に合わせてスポーン）。

> 文字の対応文字種: `A-Z` `0-9` `! ? . - + < >` とスペース。日本語はドット数が多く非対応（英数字推奨）。任意の絵柄は UI エディタで自由に作れます。

---

## 向きと自動演出

文字は「傾き(Pitch)」で向きを制御します。**0°=横向き（正面から）/ 90°相当=水平（真下から読める）**。

| コマンド | 説明 |
|---|---|
| `/drone orient <グループ> upright` | 横向き（正面から見る） |
| `/drone orient <グループ> flat` | 水平（真下から正しく読める） |
| `/drone orient <グループ> tilt <度>` | 任意角度に傾ける |
| `/drone spin <グループ> <度/秒>` / `off` | 連続回転（全方向から見せる。多少のラグあり） |
| `/drone present <グループ> [保持秒]` / `off` | **自動演出**：下向き→正面→右→裏→左 を順に**静止して保持**しループ |

**おすすめ:** `present` は各ポーズで停止して見せるため崩れにくく、全方向＋真下から綺麗に見えます。
```
/drone text show1 RUST
/drone present show1 5     # 各ポーズ5秒保持でループ
```

---

## パターン作成ツール（UIエディタ）
権限: `droneshow.use`

**ゲーム内に表示されるグリッドUIをマウスでクリックして、任意の絵柄を作るツール**です。作った絵柄は名前を付けて保存でき（`data/DroneShow_Patterns.json`）、`/drone pattern` で何度でも呼び出せます。

| コマンド | 説明 |
|---|---|
| `/dronepattern new <名前> <幅> <高さ>` | 新規作成。指定サイズの空グリッドのUIを開く（既定上限 32×32・設定で変更可） |
| `/dronepattern edit <名前>` | 保存済みパターンをUIで再編集 |
| `/dronepattern link <グループ>` | 編集中の絵柄を実機ドローンに**リアルタイムプレビュー** |
| `/dronepattern list` | 保存パターン一覧 |
| `/dronepattern delete <名前>` | 削除 |

**UIの操作:**
- 画面下の **モード** / **筆** ボタンを選んでから、グリッドのセルをクリックして描きます。
  - **モード**: `描画`（クリックで点灯）/ `消去`（クリックで消灯）/ `矩形`（2点クリックで長方形を塗りつぶし）。
  - **筆**: `1`/`2`/`3` … クリック1回で塗る範囲（1=1マス、2=3×3、3=5×5）。広い面は筆を大きく、まとまった面は矩形が速いです。
- **保存** / **クリア** / **閉じる** ボタンで操作。クリックした所だけ更新され、カーソルは固定されます。

> Rust の UI 仕様上、マウスを押したままの**ドラッグ描画はできません**。代わりに「描画モード＋大きい筆」や「矩形塗り」でクリック数を大幅に減らせます。

**使い方の流れ（例）:**
```
1) /drone spawn show1 30 35        # 表示用グループを用意（任意。無くても link で自動生成）
2) /dronepattern new smile 11 9    # 11x9 のUIエディタを開く
3) /dronepattern link show1        # 実機にリアルタイム反映しながら…
4) UIのセルをクリックして顔を描く
5) 「保存」ボタン → smile として保存
6) /drone pattern show1 smile      # いつでも呼び出して表示
```

> `link` 中はセルを塗るたびに実機のドローン編隊が即更新されるので、**実物を見ながらデザイン**できます。

### 大きいパターン（100×100 など）について
- **UIエディタ（クリック式）の上限**は `Pattern editor max grid size`（既定 32、最大 64）。これはCUIボタン数の都合で、これ以上は重く/不安定になります。100×100 のクリック編集は非現実的です。
- **より大きい/精密な絵**は、データファイル `data/DroneShow_Patterns.json` を**直接編集**すれば**サイズ無制限**で作れます（`#`/`.` の行を並べるだけ。画像から変換して貼り付けるのも可）。
- ただし**表示できるのは点灯ドット数 ≤ `Max drones per group`（既定256）**まで（点灯1つ＝1機）。100×100でも「点が少ないスパースな絵」なら表示できますが、密に光らせる大きな絵は機数・サーバー負荷の上限に当たります。

---

## ウェーブ型ミニゲーム
権限: `droneshow.admin`

| コマンド | 説明 |
|---|---|
| `/dronegame start [ウェーブ数]` | 視線の先を中心にゲーム開始 |
| `/dronegame boss [倍率]` | ボスを1体だけ出す（テスト用・サイズ指定可） |
| `/dronegame stop` | 終了 |
| `/dronegame status` | 進行状況 |

敵ドローンは**プレイヤーの周囲を編隊で旋回しながら**攻撃します。ウェーブが進むほど数が増え、最終ウェーブにボスが出現。撃墜で撃墜数が加算され、全滅でクリア。

**敵の種類:**
| タイプ | 行動 |
|---|---|
| 突撃 (Charger) | プレイヤーへ直接突っ込み、接近すると自爆 |
| 爆撃 (Bomber) | 旋回しつつプレイヤーへ C4/F1 を投射 |
| 連射型ガンナー | 近めで素早く銃撃（低威力・見える銃口/着弾） |
| スナイパー型ガンナー | 高威力・高精度の単発 |
| ショットガン型ガンナー | 近距離で散弾を一斉発射 |
| ボス（最終ウェーブ） | 大型・高耐久。常時は**銃**主体でロケットは時々。体力が減ると**激昂**し黒煙を上げてロケットを連発 |

- 銃撃はヒットスキャン（瞬間命中）。銃声＋マズルフラッシュ＋着弾エフェクトで「撃たれている」と分かります。
- ボスの大型化は `networkEntityScale` でクライアントへスケール同期（外部プラグイン不要）。

---

## 設定（config）

設定キーはすべて英語。主なもの:

### 全般・編隊・文字
| キー | 既定 | 説明 |
|---|---|---|
| `Drone prefab path` | `drone.deployed.prefab` | ドローンのプレハブ |
| `Max drones per group` | 256 | グループ最大機数（長い文字ほど必要） |
| `Default spawn height (m)` | 35 | 既定スポーン高さ（自動生成にも適用） |
| `Default formation spacing (m)` | 2.5 | 編隊間隔 |
| `Text - Dot spacing (m)` | 1.8 | 文字/パターンのドット間隔 |
| `Drone move speed override` | 30 | 追従速度（高いほど崩れにくい。0=vanilla・global） |
| `Drone altitude speed override` | 30 | 上下の追従速度（0=vanilla・global） |
| `Show drones invulnerable` | true | ショードローンを被弾無効に |
| `Disable drone-to-drone collisions` | true | 衝突無効（墜落防止） |
| `Light prefab (empty to disable)` | `simplelight.prefab` | 添付ライト |

### ミニゲーム（抜粋）
| キー | 既定 | 説明 |
|---|---|---|
| `Minigame - Arena radius (m)` | 40 | 戦闘範囲 |
| `Minigame - Attack height (m)` | 20 | 敵のホバリング高度 |
| `Orbit - Radius / Speed` | 12 / 0.8 | 旋回の半径・速さ |
| `Spawn count - <Type> base` / `... per wave` | — | タイプ別の初期数・ウェーブ増加 |
| `Charger - Health` / `Bomber - Health` 等 | 30 / 60 | **タイプ別 HP** |
| `Gunner Rapid/Sniper/Shotgun - Health/Damage/...` | — | ガンナー別の攻撃 |
| `Boss - Health` / `Boss - Size multiplier` | 1500 / 3 | ボスの耐久・大きさ |
| `Boss Rocket - Interval normal/enraged` | 10 / 3 | ロケット間隔（通常/激昂） |
| `Boss - Enrage smoke effect / Smoke interval` | rocket_smoke / 0.6 | 激昂時の黒煙 |

> **重要**: 設定キーや既定値を変えても、**既存 `DroneShow.json` の値は引き継がれます**。新しい既定（例: `Max drones per group=256`、`Default spawn height=35`）を反映するには、その値を手動で書き換えるか、config を削除して再生成してください。

---

## 多言語対応（ローカリゼーション）

このプラグインは uMod の [Localization API](https://umod.org/documentation/api/localization) を使っており、プレイヤーへのチャットメッセージとUIエディタのボタン表記が**プレイヤーの言語設定**に応じて切り替わります。

- **既定言語は英語（`en`）**。プレイヤーの言語が日本語なら**日本語（`ja`）**で表示されます。
- 言語ファイルはプラグイン初回ロード時に自動生成されます:
  - `oxide/lang/en/DroneShow.json`（Carbon は `carbon/lang/en/DroneShow.json`）
  - `oxide/lang/ja/DroneShow.json`（Carbon は `carbon/lang/ja/DroneShow.json`）
- **他言語を追加**するには、`lang/<コード>/DroneShow.json` を作って各キーを翻訳するだけです（例: `de`, `fr`, `ru`, `zh-CN`）。サーバーの既定言語は `oxide/config/oxide.json`（または `carbon/config/Carbon.json`）の言語設定で変えられます。
- 文言を変更したい場合は、該当言語の `DroneShow.json` 内の値を編集してください（キーは変更しない）。

---

## 仕組み（技術メモ）

- **飛行制御**: `Drone.targetPosition` に座標をセットすると、ゲーム側の物理ナビで自動的に飛ぶ（`DeliveryDrone` と同方式・ネット同期も自然）。各ドローンに `DroneAgent` を付与し 0.1 秒タイマーで更新。
- **追従の高速化**: `Drone.movementSpeedOverride`/`altitudeSpeedOverride`（global ServerVar）を上げて、移動・回転中の編隊/文字の崩れを抑制。
- **衝突回避**: `body.detectCollisions=false` で互いに/地形にぶつからず墜落しない（弾はレイキャストなので撃墜は可能）。
- **文字配置**: 5×7 ドットフォント or 任意の配列を中心相対の点に展開し、各機へ割当。向きは表示側の **Pitch（WorldSlot で傾ける）** で付与。余剰機は Parked にして地中退避＋消灯。
- **ボス大型化**: `transform.localScale` ＋ **`networkEntityScale=true`** でクライアントへスケール同期（`Spawn()` 前に設定）。外部プラグイン不要。
- **銃撃**: ヒットスキャン＋銃口/着弾エフェクト。遮蔽判定は地形・建築のみ。
- **乗っ取り防止**: `OnEntityControl` フックで当プラグインのドローンを操縦不可に。

---

## テスト手順・注意点

1. 配置 → コンソールに `Loaded plugin DroneShow` を確認。
2. `/drone text t1 RUST` → 自動で機体が出て文字が表示されるか。
3. `/drone present t1 5` → 各方向＋真下から見えるか。
4. `/dronepattern new heart 9 8` → エディタでセル/保存/クリア/閉じるが動くか。`link t1` で実機プレビュー。
5. `/dronegame boss 5` → ボスのサイズ・銃・ロケット・激昂の黒煙。
6. `/dronegame start 3` → ウェーブ全体（各敵タイプ）。
7. `/drone clear all` `/dronegame stop` で後片付け。

**注意:**
- **god モードだとダメージを受けません**。ミニゲームのテストは通常状態で。
- エフェクト/プレハブのパスが環境に無い場合は無効化されます（コンソール警告）。`/drone prefabs <キーワード>` で実在パスを探して config を差し替え可能。
- 文字をきれいに見せたいときは、`spin`（連続回転）より `present`（静止ポーズ）を推奨。
- 既存 config の `Max drones per group` / `Default spawn height` は手動で上げてください（前述）。

問題があれば、サーバーコンソールのエラーログをそのまま共有してください。
