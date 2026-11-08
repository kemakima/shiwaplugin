# ClothWrinkle（服のしわ）v0.5.0

体を曲げたときに、服（レオタードなど）の縮んだ所へリアルタイムにしわを描き足す、KKS（コイカツ！サンシャイン）CharaStudio 用の BepInEx プラグインです。
しわは法線マップ（陰影）と、KK 系シェーダーの「描き込みマスク」（アニメ調の線・影）に描きます。服の輪郭は変わりません。

[English below](#english)

## 使い方
1. `KKS_ClothWrinkle.dll` を `BepInEx\plugins` に入れて Studio を起動します（KKSAPI 1.30 以上が必要です）。
2. キャラを選び、「アニメ → 現在の状態」の **「服のしわ」** で「しわを付ける」をオンにします。
3. ポーズやアニメーションに合わせて、しわの濃さがリアルタイムに変わります。

| 項目 | 内容 |
|---|---|
| 強さ | しわの陰影（法線マップ）の濃さ |
| しわの細かさ | 大きいほど折り目の間隔が狭くなる（1〜20、既定 5） |
| 曲げへの反応 | 大きいほど、少しの縮みでも濃いしわになる |
| 小さな縮みを無視 | これ以下の縮みはしわにしない |
| 折り目の鋭さ | 大きいほど細く鋭い折り目（アニメ調の線も細くなる） |
| 深い曲げで増えるしわ | 深く曲げたとき、折り目が深くなり、間に短い折り目が増える（v0.5） |
| しわの線 (アニメ調) | 折り目の谷に描く線の濃さ（v0.5、KK 系シェーダーのみ） |
| しわの影 (アニメ調) | 折り目の上側に描く影の濃さ（v0.5、KK 系シェーダーのみ） |
| しわ模様 | 模様のパターンを変える |
| リアルタイム更新 | オフにすると止まる。そのときは「今のポーズで更新」で描き直す |
| 今のポーズで更新 | 押すたびに、今のポーズでしわの濃さを計算し直す |
| 今のポーズを基準にする(通常は自動) | 通常は「体型込みの直立姿勢」が自動で基準になる。特定のポーズを「しわ無し」にしたいときだけオン。v0.5 からシーンに保存されます |

「服のしわ (対象)」で、トップス・ボトムス・インナー上下・手袋・パンスト・靴下・靴を個別にオン/オフできます（靴は標準でオフ）。
設定はキャラごとにシーン・キャラカードへ保存されます。

## v0.5.0 の変更
- **アニメ調のしわ線・しわ影**: KK 系の服シェーダー（`Koikano/main_clothes_*`、`Shader Forge/main_*`）の `_DetailMask` は、G が「描き込みの影」、B が「描き込みの線」として使われます。ここに、折り目の谷に沿った先細りの線と、折り目の上側の影を描きます。トゥーン調の陰影でもしわが見えやすくなります。
  - 線は、そのマテリアルの `DetailBLineG` がオンのときだけ出ます（オフのときは B チャンネルに触りません）。線の濃さはゲームの「線の太さ」の設定にも左右されます。
  - 影の効き方はマテリアルの `ShadowExtend` に左右されます（0 だと影は出ません）。
  - Az/Standard 系のシェーダーは `_DetailMask` の意味が違うので、法線マップのしわだけです。
- **深く曲げると折り目が増える**: しわの模様を2層にしました。浅い曲げでは今まで通り、濃さが一定を超えると、折り目が深くなり、間に短い折り目が加わります。
- **スライダーで固まらない**: しわ模様の画像作り（1枚 0.3〜0.6 秒かかっていた）を別スレッドに移しました。
- **UV の継ぎ目の細い線の対策**: UV の島の外周を外側へ 1.5 ピクセル広げて描きます。
- **描画の高速化**: しわの合成を、三角形ごとの GL 描画からメッシュ一括描画に変えました。起動後最初の描画で動くか自動で確かめ、だめなら従来の GL 描画に戻ります（ログに「描画方法」と出ます）。
- **「今のポーズを基準にする」をシーンに保存**: 基準にしたポーズ（骨の回転）を保存し、シーンを開き直したときや服を替えたときに作り直します。

## 仕組み
重い処理は最初に1回だけ行い、毎フレームは「どこがどれだけ縮んでいるか」だけを更新します。

**最初に1回（有効にしたとき・模様のスライダーを動かしたとき。v0.5 から別スレッド）**
1. 横じわの模様を、しわが最大に出た状態で作ります。向きは体の長さ方向（胴体・脚は上下、腕は腕のボーンの向き）と直角で、位相は「体の軸に沿った座標」（腕は肩からの長さ）で決めます。
2. 法線マップ用（1層目・深い曲げ用の2層目）と、アニメ調の線・影用の画像を作ります。
3. 服のメッシュを UV の位置に広げたメッシュ（外周のすそ付き）を作ります。

**毎フレーム（最大 30 回/秒、服が画面に映っていて、主要な関節が動いたときだけ）**
1. 服の今の形（BakeMesh）と基準の形を三角形ごとに比べ、体の軸方向の縮みを求めます。インナー上下など別の服をまたいで、境目の頂点は共通に扱います。
2. 頂点ごとの「しわの濃さ」を UV メッシュの頂点色に入れます。
3. GPU で「元の画像」の上に「しわ最大の画像」を濃さで重ね、`_NormalMap`（と `_DetailMask`）の RenderTexture に描きます。

折り目の位置は固定で、曲げに合わせて濃くなったり消えたりします。

## 設定ファイル（`BepInEx\config\zuki.kks.clothwrinkle.cfg` または F1）
- `Wrinkle map resolution`（既定 512）: しわを計算する解像度の下限（出力の大きさに合わせて最大 1024 まで自動で上げる）
- `Normal map max size`（既定 1024）: 書き込む画像の最大サイズ（元が小さい画像は長い辺 1024 まで拡大）
- `Max updates per second`（既定 30）: リアルタイム更新の最大回数。下げるほど軽い
- `Fast mesh drawing`（既定オン）: メッシュ一括描画を使う（v0.5）。オフで常に従来の GL 描画
- `Log strain stats`: オンにすると、5 秒ごとに更新1回あたりの計算時間をログに出します（調整用）
- `Dump debug textures`: オンにすると、次の更新で画像（元/しわ最大/結果）を `UserData\ClothWrinkle_debug` に保存します

ログは `UserData\ClothWrinkle_log.txt` にも出ます。

## 制限
- 陰影と描き込みだけなので、横から見た輪郭は平らなままです。
- `_NormalMap` を持つシェーダーのみ対象です（Koikano/main_clothes 系、Shader Forge/main 系、Az/Standard 系は持っています）。アニメ調の線・影は KK 系シェーダーのみです。
- 物理演算の布（Cloth コンポーネント付きのスカートなど）は対象外です。
- DirtFX の「泥の立体感」も `_NormalMap` を書き換えるので、同じ服に両方使うと取り合いになります。
- しわは横じわのみです。折り目の位置はポーズで動きません（濃さだけが変わります）。

## 更新履歴（抜粋）
- v0.5.0: アニメ調の線・影、深い曲げの2層目、別スレッド化、UV 継ぎ目対策、メッシュ一括描画、基準ポーズの保存
- v0.4.1: 小さい画像（手袋など）を拡大して描く、腕のしま模様を「肩からの長さ」で決める
- v0.4.0: 基準の自動化（体型込みの直立姿勢）、体の軸方向の縮みだけを見る、静止中は計算しない
- v0.3.x: リアルタイム版、インナー上下の境目対策、×印のしわの修正
- v0.2.x: 横じわに統一、細かさの既定値を 5 に

## ビルド（Windows）
```bat
cd KKS_ClothWrinkle
dotnet build -c Release -p:GamePath="D:\KoikatsuSunshine"
```

---

## English

ClothWrinkle is a BepInEx plugin for Koikatsu Sunshine CharaStudio. It adds pose-dependent wrinkles, in real time, to tight clothes such as leotards, gloves and tights.

**How to use:** Put `KKS_ClothWrinkle.dll` in `BepInEx\plugins` (KKSAPI 1.30+ required). Select a character, then go to *Anim → Current State → 服のしわ (cloth wrinkles)* and turn on 「しわを付ける」 (enable).

**What it does**
- Each frame, it compares the skinned clothing mesh (BakeMesh) with a reference shape. By default the reference is the character's own body shape in the bind pose. It measures compression along the body axis: up/down for the torso and legs, along the bones for the arms.
- Compressed areas get horizontal folds that wrap around the body. A fold pattern is pre-built at full strength, and the GPU blends it in by a per-vertex amount, rendered in UV space into a RenderTexture.
- It writes to `_NormalMap` (shading). On KK toon shaders (`Koikano/main_clothes_*`, `Shader Forge/main_*`) it also writes to `_DetailMask`, which those shaders use for hand-drawn shadow (G) and line (B). The result is anime-style crease lines and shadows.
- Deep bends make the folds deeper and add short extra folds in between.
- Pattern generation runs on a worker thread. Per-frame work is skipped while the main joints are not moving.
- Settings are saved per character in scenes and cards. A pose set as the custom reference is saved with them.

**Sliders:** Strength, fold detail, sensitivity, ignore small compression, sharpness, deep-bend folds, toon line, toon shadow, pattern seed, real-time on/off, update now, use current pose as reference. A second category toggles each clothing slot.

**Limitations:** Shading only; the silhouette is unchanged. Clothes with a Cloth component are skipped. DirtFX's mud normals also write to `_NormalMap`, so the two plugins conflict on the same garment.

**Build:** `dotnet build -c Release -p:GamePath="<KKS folder>"` in `KKS_ClothWrinkle`.

License: MIT
