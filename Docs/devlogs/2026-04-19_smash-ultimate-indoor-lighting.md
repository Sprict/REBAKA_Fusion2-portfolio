# Smash Ultimate 級 屋内ライティング (URP / Switch 級 PC 向け)

**Date:** 2026-04-19
**Branch:** feature/indoor-probe-placer
**Scope:** Lighting / Post-Processing / Reflection Probe (Main_Backup.unity, URP)
**計画:** `C:\Users\raren\.claude\plans\noble-leaping-wirth.md`

## 問題

`Main_Backup.unity` の屋内ライティングが不自然だった。

- **青空 bleed:** Skybox Procedural の空色が屋内壁にまで回り込み、"冷たい青い洞窟" に見えていた
- **遠くも均一に明るい:** Baked GI の品質不足（Lightmap Resolution=2、Reflection Probe=64）で減衰感が出ない
- **反射が単調:** Reflection Probe 64² だと屋内鏡面がほぼ空色に染まる
- **Post-Processing がニュートラル:** Tonemap=Neutral / 色補正なしで「仮組みの映像」

目標は任天堂『大乱闘スマッシュブラザーズ SPECIAL』風の「鮮やかで映える屋内」。ただし Nintendo Switch 級 PC (Tegra X1/Maxwell, 4GB RAM 想定) で 60fps 維持する制約 → **HDRP 採用不可、URP Forward+ のまま品質を押し上げる** 方針。

## なぜこのアプローチか

### 採用: 「HDRI Ambient + 高密度 Lightmap + Mixed 屋内 Point Light + ACES 色設計」

5 Phase に分割:

| Phase | 施策 | 狙い |
|---|---|---|
| A | Skybox を Poly Haven HDRI `kloppenheim_06_2k.exr` に差替え、Ambient Intensity 調整 | 青空 bleed を排除、HDRI のニュートラル空気で屋内外を一体化 |
| B | Lightmap Resolution 2→10、GPU Lightmapper、AtlasSize 2048、Directional bounceIntensity 1→1.5 | 屋内で Indirect bounce が効く下地を作る |
| C | Reflection Probe Resolution 64→256、全 9 個再ベイク | 鏡面反射の空色一色状態を解消、屋内の質感を出す |
| D | Tonemapping Neutral→**ACES**、ColorAdjustments (+Post Exposure 1.0 EV, +Contrast 15, +Saturation 10, Color Filter RGB(1.02, 1.00, 0.98))、Bloom threshold 1.1/intensity 0.25 | 任天堂系「鮮やかメリハリ」へ。Bloom は控えめで王道ルック |
| E | URP SSAO Intensity 0.5→1.0、Radius 0.25→0.35、SampleCount 4→8、Downsample OFF | コンタクト影で立体感 |

Phase F (Bake + 検証) は A–E 完了後に一括実施。

### 不採用の代替案

1. **HDRP 移行** — 光源/GI の表現力は最大だが、Switch 級 PC に乗らない。URP Forward+ に留めた。
2. **Fully Realtime GI (URP 非対応だがライトプローブで近似)** — ベイク時間は不要だがランタイム 60fps の余裕がない。全 Baked で決めた。
3. **Point Light の全面 Baked 化** — direct が完全に焼き固まるので、動くキャラへの陰影が Light Probe 任せになり「貼り付き感」が出る。Mixed で direct はリアルタイム、indirect のみ bake を選択。
4. **HDRI 4K** — Bake 時の GPU メモリ圧迫。2K で十分。
5. **Reflection Probe 512** — VRAM 圧迫のリスク。256 を上限に。

### Mixed Lighting Mode 選択

`mixedBakeMode = Shadowmask` を採用（既に設定済）。
- **理由:** 動的キャラが静的屋内 prop に落とす影は距離で shadowmap → shadowmask に自然フェードする。Switch 想定で遠距離シャドウを切ってもデグラデしない。
- **代替 (Baked Indirect):** direct 影は全てリアルタイム shadowmap のため、遠距離 GPU コストが高い。却下。
- **代替 (Subtractive):** ライト複数時の影品質が破綻するので不採用。

## 仕組み

### Phase A — HDRI Skybox

```
Poly Haven `kloppenheim_06_2k.exr`
  ├─ TextureImporter: Shape=Cube, Mapping=LatitudeLongitude,
  │                   sRGBTexture=false (HDR), Filtering=Trilinear
  ├─ Sky_HDRI.mat (Skybox/Cubemap): _Tex=<cubemap>, _Exposure=2.0
  └─ RenderSettings.skybox = Sky_HDRI.mat
     AmbientMode = Skybox
     AmbientIntensity = 1.8
     ReflectionIntensity = 1.2
DynamicGI.UpdateEnvironment()
```

### Phase B — Lightmap 品質

```
LightingSettings.asset (RebakaFusionLighting):
  bakedGI           = true
  realtimeGI        = false      // URP + Switch で realtime GI は非推奨
  lightmapResolution = 10          // 2 → 10 (5倍)
  lightmapMaxSize   = 2048        // 1024 → 2048
  maxBounces        = 3            // 2 → 3
  indirectScale     = 1.2          // 1.0 → 1.2
  lightmapper       = ProgressiveGPU
Directional Light:
  bounceIntensity  = 1.5   // bake 経由で屋内への漏れ光を増強
  shadowType       = Soft
  shadowResolution = VeryHigh
```

### Phase C — Reflection Probe 256

```
foreach AutoProbe in scene:
  resolution = 256        // 64 → 256 (4倍、容量16倍)
  hdr        = true
  mode       = Baked
  intensity  = 0.9
  blendDistance = 0       // 同時サンプル 1 個に抑える
```

### Phase D — Post-Processing (ACES + Color Adjustments)

```yaml
# Assets/Settings/SampleSceneProfile.asset (VolumeProfile)
Tonemapping:
  mode: ACES              # Neutral → ACES
Bloom:
  threshold: 1.1           # 光源以外は拾わない (王道ルック)
  intensity: 0.25          # 控えめ
ColorAdjustments (NEW):
  postExposure: +1.0       # 屋内暗部を EV で底上げ
  contrast: +15
  saturation: +10          # 任天堂系の彩度
  colorFilter: (1.02, 1.00, 0.98)  # わずか暖色
Vignette:
  intensity: 0.25          # そのまま維持
```

**ColorAdjustments YAML 直接追加の経緯:** `profile.Add<ColorAdjustments>()` を uLoopMCP 経由で呼ぶと、戻り値の VolumeParameter に `EditorUtility.SetDirty` した際に `target destroyed` 例外が発生。TryGet→SerializedObject も `m_OverrideState` 書き換え直後に参照失効。Roslyn worker 環境下の UnityEditor API に潜む非同期 GC タイミングの問題と推定 [※推測]。回避策として **`.asset` YAML に ColorAdjustments エントリを直接追記** し `AssetDatabase.ImportAsset(ForceUpdate|ForceSynchronousImport)` で反映させた。

### Phase E — SSAO 強化

```
URP-Balanced-Renderer.asset の ScreenSpaceAmbientOcclusion (SerializedObject 経由):
  Downsample = false
  Intensity = 1.0
  Radius = 0.35
  SampleCount = 8
```

### Phase F — 屋内 Point Light 追加 & Bake

Phase B の Indirect だけでは屋内が暗いままだったため、Indoor Probe Placer の Reflection Probe 位置 9 箇所に Mixed Point Light を自動配置。

```
[AutoLight] IndoorPointLights (parent GameObject)
  └─ [AutoLight] Point_0..Point_8
       type = Point, intensity = 8, range = 10
       color = (1.00, 0.82, 0.55)       // 暖色 torch
       bounceIntensity = 1.5
       lightmapBakeType = Mixed
       shadows = Soft
```

Point_7 だけ座標 (-7.75, 2.90, -0.40) が `Map_Indoor/Props/Sphere_001` (Lit/Gray) の 1.5m 以内で、球が真っ橙色に染まり「謎のオレンジ球」問題が発生。Point_7 を (-7.75, 4.20, -2.50) に退避し intensity=5/range=8 に下げて解消。

## 判断の記録

### なぜ Post Exposure +1.0 EV まで上げたか

A–E 実施後も Play Mode の屋内が暗く見えた。原因は次の 3 つが重なっていた。
1. Directional Light が **Realtime** のため屋根越しの漏れ光が Lightmap に焼かれていない
2. Ambient Mode=Skybox は天空方向サンプリング → 屋根付き屋内に届かない
3. Bloom threshold=1.1 が高く暗部を持ち上げない

Directional Light を Mixed 化すれば再ベイクで漏れ光が焼けるが、Bake 30 分超えのコストを避け、**Post Exposure で画面全体を +1.0 EV (= 2倍) 底上げ**する方針を取った。ACES トーンマップが高輝度を自然に圧縮するので、白飛びは抑えられる。

- **+0.6 EV:** 変化が目視できない
- **+1.5 EV:** やや白飛び気味
- **+1.0 EV:** Torch 暖色と石壁青白のコントラストが効いた Dungeon 風。**採用**

### 残タスク (将来の改善余地)

- [ ] Directional Light を Mixed 化して再ベイク → Post Exposure を下げる本格路線
- [ ] 屋外視点の FPS 実機計測 (Switch build)
- [ ] SSAO 有効化時のバンディング確認 (暗部が階調崩れしていないか)
- [ ] Reflection Probe 256 の VRAM 実測 (9 個 × 256² HDR BC6H ≒ 7 MB 目安)

## 自力再実装チェックリスト

新規 URP プロジェクトで Smash 風屋内ルックを再現する場合:

1. [ ] Skybox を HDRI (2K 相当) に差替え、`RenderSettings.ambientIntensity` で明るさ調整 (1.5〜2.0)
2. [ ] LightingSettings で `lightmapResolution >= 10`, `maxBounces = 3`, `indirectScale >= 1.2`, GPU Lightmapper 選択
3. [ ] 屋内 Point Light は **Mixed** で配置、`bounceIntensity >= 1.5`、暖色 torch なら RGB(1.0, 0.82, 0.55)
4. [ ] Directional Light は最終的に Mixed 推奨 (今回は Realtime のまま妥協)
5. [ ] Reflection Probe 256 HDR Baked、部屋数分配置
6. [ ] VolumeProfile に **ACES** Tonemapping + ColorAdjustments (postExposure +1.0 EV, saturation +10, contrast +15) + 控えめ Bloom (threshold 1.0〜1.2, intensity 0.25〜0.4)
7. [ ] SSAO Intensity 1.0, Radius 0.35, SampleCount 8
8. [ ] Bake 後に LightingData.asset 周辺を AssetDatabase.ForceUpdate で reimport (Unknown error 対策 [※推測])

## 参考

- Plan: `C:\Users\raren\.claude\plans\noble-leaping-wirth.md`
- 前段 devlog: `2026-04-17_indoor-probe-placer.md` (Light/Reflection Probe 配置自動化)
- Poly Haven HDRI ライセンス: CC0 (検証可能オープンソース)
