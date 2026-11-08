using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KKAPI;
using KKAPI.Chara;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>
    /// キャラごとのしわ処理 (リアルタイム版)。
    /// 服のマテリアルごとに、横じわ模様 (最大の濃さ) を1回だけ作り、
    /// 毎フレームは頂点ごとの濃さだけを計算して GPU で法線マップに重ねる。
    /// </summary>
    public class WrinkleController : CharaCustomFunctionController
    {
        private const string NormalProp = "_NormalMap";

        public WrinkleSettings Settings { get; private set; } = new WrinkleSettings();
        public bool HasReference { get; private set; }

        private class Target
        {
            public Material Material;
            public List<MeshPart> Parts = new List<MeshPart>();
            public TexelBinding Binding;
            public float[] Pattern;        // しわの向き (テンソル)
            public string PatternKey;
            public Texture OrigNormal;
            public Color32[] OrigPx;
            public int W, H;
            public Texture2D BaseTex;      // 元の法線 (RGB に変換)
            public Texture2D FullTex;      // 元の法線 + しわ最大
            public RenderTexture Output;
            public bool KeywordWasOn;
            public bool Failed;
            public bool Logged;
            public bool Applied;           // 一度でもマテリアルに入れたか
        }

        private readonly Dictionary<Material, Target> _targets = new Dictionary<Material, Target>();
        private string _layoutKey = "";
        private float _heightRef = 1.5f;

        private bool _rebuild, _pattern, _frame;
        private float _applyAt, _nextCheck, _nextFrame;

        // ---------------- 公開API ----------------

        public void Modify(Action<WrinkleSettings> change)
        {
            var before = Settings.Clone();
            change(Settings);
            if (!Settings.Enabled) { RestoreAll(); return; }
            if (!before.Enabled || before.SlotMask != Settings.SlotMask) { MarkDirty(rebuild: true, delay: 0.1f); return; }
            if (before.PatternKey() != Settings.PatternKey()) MarkDirty(pattern: true, delay: 0.3f);
            else _frame = true; // 濃さだけの変更は次のフレームで反映
        }

        public void ForceUpdate()
        {
            if (Settings.Enabled) _frame = true;
        }

        /// <summary>今のポーズを「しわ無し」の基準にする (false で元の形に戻す)。シーンには保存されない。</summary>
        public void SetReference(bool on)
        {
            if (!Settings.Enabled) { HasReference = false; return; }
            if (on == HasReference) return;
            HasReference = on;
            foreach (var t in _targets.Values)
                foreach (var p in t.Parts)
                {
                    Vector3[] rf = p.AutoRef; // オフ = 自動の基準 (体型込みの直立姿勢) に戻す
                    if (on)
                    {
                        try
                        {
                            if (p.Baked == null) p.Baked = new Mesh();
                            p.Renderer.BakeMesh(p.Baked);
                            var v = p.Baked.vertices;
                            if (v.Length == p.Rest.Length) rf = v;
                        }
                        catch (Exception e) { WrinklePlugin.Log.LogWarning("基準ポーズを記録できません: " + e.Message); }
                    }
                    if (p.TriL1 != null) Realtime.SetReference(p, rf);
                }
            _frame = true;
        }

        private void MarkDirty(bool rebuild = false, bool pattern = false, float delay = 0f)
        {
            _rebuild |= rebuild;
            _pattern |= pattern;
            _applyAt = Time.unscaledTime + delay;
        }

        // ---------------- KKAPI ----------------

        protected override void OnCardBeingSaved(GameMode currentGameMode)
        {
            SetExtendedData(Settings.IsDefault() ? null : Settings.Save());
        }

        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            if (maintainState) return;
            RestoreAll();
            Settings = WrinkleSettings.Load(GetExtendedData());
            if (Settings.Enabled) MarkDirty(rebuild: true, delay: 0.5f);
        }

        protected override void OnCoordinateBeingLoaded(ChaFileCoordinate coordinate, bool maintainState)
        {
            if (Settings.Enabled) MarkDirty(rebuild: true, delay: 0.5f);
        }

        protected override void Update()
        {
            base.Update();
            if (!Settings.Enabled) return;
            float now = Time.unscaledTime;

            if (now >= _nextCheck)
            {
                _nextCheck = now + 1f;
                try { Check(); }
                catch (Exception e) { WrinklePlugin.Log.LogWarning(e); }
            }

            if ((_rebuild || _pattern) && now >= _applyAt)
            {
                bool rebuild = _rebuild;
                _rebuild = _pattern = false;
                try
                {
                    if (rebuild || _targets.Count == 0) Rebuild();
                    foreach (var t in _targets.Values) BuildPattern(t);
                }
                catch (Exception e) { WrinklePlugin.Log.LogError("しわ模様の作成に失敗: " + e); WrinklePlugin.FileLog("しわ模様の作成に失敗: " + e); }
                _frame = true;
            }

            bool due = Settings.Realtime && now >= _nextFrame;
            if (due || _frame)
            {
                bool force = _frame;
                _frame = false;
                float rate = Mathf.Max(1f, WrinklePlugin.MaxRate.Value);
                _nextFrame = now + 1f / rate;
                UpdateAllFrames(force);
            }
        }

        protected override void OnDestroy()
        {
            RestoreAll();
            base.OnDestroy();
        }

        // ---------------- 変化の検出 ----------------

        private void Check()
        {
            if (LayoutKey() != _layoutKey) { MarkDirty(rebuild: true, delay: 0.2f); return; }
            // ゲームや他のプラグインが法線マップを差し替えた → それを元にして模様を作り直す
            foreach (var t in _targets.Values)
                if (t.Applied && t.Output != null && t.Material != null && t.Material.GetTexture(NormalProp) != t.Output)
                { t.PatternKey = null; MarkDirty(pattern: true, delay: 0.1f); }
        }

        // ---------------- 対象の収集 ----------------

        private IEnumerable<KeyValuePair<SkinnedMeshRenderer, int>> CollectSlots()
        {
            var objs = ChaControl.objClothes;
            if (objs == null) yield break;
            for (int ci = 0; ci < objs.Length; ci++)
            {
                if (objs[ci] == null || !Settings.HasSlot(ci)) continue;
                foreach (var r in objs[ci].GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (r == null || !r.enabled || r.sharedMesh == null) continue;
                    if (r.GetComponent<Cloth>() != null) continue; // 物理演算の布は対象外
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && mats[i].HasProperty(NormalProp))
                            yield return new KeyValuePair<SkinnedMeshRenderer, int>(r, i);
                }
            }
        }

        private string LayoutKey()
        {
            var sb = new StringBuilder();
            foreach (var s in CollectSlots())
                sb.Append(s.Key.GetInstanceID()).Append(':').Append(s.Value).Append(':')
                  .Append(s.Key.sharedMaterials[s.Value].GetInstanceID()).Append(':').Append(s.Key.sharedMesh.GetInstanceID()).Append(';');
            return sb.ToString();
        }

        // ---------------- 準備 ----------------

        private void Rebuild()
        {
            RestoreAll();
            _layoutKey = LayoutKey();
            _heightRef = BodyHeight();
            foreach (var s in CollectSlots())
            {
                var mat = s.Key.sharedMaterials[s.Value];
                if (!_targets.TryGetValue(mat, out var t)) { t = new Target { Material = mat }; _targets[mat] = t; }
                t.Parts.Add(new MeshPart { Renderer = s.Key, SubMesh = s.Value });
            }
            foreach (var t in _targets.Values.ToList())
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    WrinkleBaker.PrepareParts(t.Parts);
                    foreach (var p in t.Parts) Realtime.PreparePart(p);
                    t.Binding = WrinkleBaker.BuildBinding(t.Parts, BindingRes(t.Material), _heightRef);
                    t.Pattern = Realtime.PatternTensor(t.Parts);
                    if (WrinklePlugin.DebugLog.Value)
                        WrinklePlugin.Log.LogInfo($"しわの対象: {t.Material.name} ({t.Material.shader.name}) 準備 {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception e)
                {
                    WrinklePlugin.Log.LogWarning($"メッシュを読めません ({t.Material.name}): {e.Message}");
                    WrinklePlugin.FileLog($"メッシュを読めません ({t.Material.name}): {e}");
                    Restore(t);
                    _targets.Remove(t.Material);
                }
            }
            // 服をまたいだ頂点の共通番号 (インナー上下の境目対策)。許容差は体の高さの 0.15%
            var all = new List<MeshPart>();
            foreach (var t in _targets.Values) all.AddRange(t.Parts);
            try
            {
                int n = Realtime.GlobalWeld(all, _heightRef * 0.0015f);
                int verts = 0; foreach (var p in all) verts += p.Rest.Length;
                WrinklePlugin.FileLog($"服をまたいだ頂点の統合: {verts} 頂点 → {n}");
            }
            catch (Exception e) { WrinklePlugin.FileLog("頂点の統合に失敗: " + e); }
            // 体型だけ反映した直立姿勢を自動で基準にする (立っているだけでしわが出ないように)
            int ok = 0, total = 0;
            foreach (var p in all)
            {
                total++;
                try
                {
                    p.AutoRef = Realtime.BakeInBindPose(p.Renderer, ref p.Baked);
                    if (p.AutoRef != null && p.TriL1 != null) { Realtime.SetReference(p, p.AutoRef); ok++; }
                }
                catch (Exception e) { WrinklePlugin.FileLog($"直立姿勢の計算に失敗 ({p.Renderer.name}): {e.Message}"); }
            }
            WrinklePlugin.FileLog($"自動の基準 (体型込みの直立姿勢): {ok}/{total} パーツ");
            _poseSig = float.NaN;
            HasReference = false;
        }

        /// <summary>しわ模様 (最大の濃さ) と元の法線の画像を作る。重いので設定が変わったときだけ。</summary>
        private void BuildPattern(Target t)
        {
            if (t.Binding == null || t.Binding.Count == 0) return;
            var key = Settings.PatternKey();

            var cur = t.Material.GetTexture(NormalProp);
            bool origChanged = t.Output == null || cur != t.Output;
            if (origChanged)
            {
                t.OrigNormal = cur;
                t.OrigPx = null;
                t.Applied = false;
                t.KeywordWasOn = t.Material.IsKeywordEnabled("_NORMALMAP");
            }
            if (!origChanged && t.PatternKey == key && t.FullTex != null) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            OutputSize(t.OrigNormal != null ? t.OrigNormal : t.Material.mainTexture, out int w, out int h);
            if (t.OrigPx == null || t.W != w || t.H != h)
            {
                t.W = w; t.H = h;
                t.OrigPx = t.OrigNormal != null ? WrinkleBaker.ReadLinear(t.OrigNormal, w, h) : null;
                t.BaseTex = Upload(t.BaseTex, BasePixels(t.OrigPx, w, h), w, h, "ClothWrinkle_base", t.OrigNormal);
            }

            var height = WrinkleBaker.BuildHeight(t.Binding, t.Pattern, Settings, true);
            var full = WrinkleBaker.Compose(height, t.Binding.Res, t.OrigPx, w, h, Settings.Strength);
            t.FullTex = Upload(t.FullTex, full, w, h, "ClothWrinkle_full", t.OrigNormal);
            t.PatternKey = key;

            if (t.Output == null || t.Output.width != w || t.Output.height != h)
            {
                if (t.Output != null) { t.Output.Release(); Destroy(t.Output); }
                t.Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "ClothWrinkle_normal", useMipMap = true, autoGenerateMips = true, anisoLevel = 4,
                    wrapMode = t.OrigNormal != null ? t.OrigNormal.wrapMode : TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear
                };
                t.Output.Create();
            }
            t.Failed = false;
            t.Logged = false;
            WrinklePlugin.FileLog($"{t.Material.name}: しわ模様を作成 {w}x{h} ({sw.ElapsedMilliseconds}ms), 有効テクセル {t.Binding.Count}");
            if (WrinklePlugin.DebugLog.Value)
                WrinklePlugin.Log.LogInfo($"{t.Material.name}: しわ模様 {w}x{h} {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// 出力 (法線マップ) のサイズ。元の画像に合わせるが、小さい画像 (手袋の 512x256 など) は
        /// しわがギザギザになるので、長い辺が 1024 になるまで縦横比のまま拡大する。上限は設定値。
        /// </summary>
        private static void OutputSize(Texture src, out int w, out int h)
        {
            w = src != null ? src.width : 1024; h = src != null ? src.height : 1024;
            int max = WrinklePlugin.OutputMax.Value;
            int min = Mathf.Min(1024, max);
            while (Mathf.Max(w, h) < min) { w *= 2; h *= 2; }
            while (w > max || h > max) { w = Mathf.Max(1, w / 2); h = Mathf.Max(1, h / 2); }
        }

        /// <summary>しわの形を計算する解像度。出力の長い辺に合わせる (設定値〜1024)。</summary>
        private static int BindingRes(Material m)
        {
            var src = m.GetTexture(NormalProp) ?? m.mainTexture;
            OutputSize(src, out int w, out int h);
            int res = Mathf.NextPowerOfTwo(Mathf.Max(w, h));
            return Mathf.Clamp(res, WrinklePlugin.MapResolution.Value, Mathf.Max(1024, WrinklePlugin.MapResolution.Value));
        }

        private static Color32[] BasePixels(Color32[] orig, int w, int h)
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                if (orig == null) { px[i] = new Color32(128, 128, 255, 255); continue; }
                var o = orig[i];
                float nx = (o.r / 255f) * (o.a / 255f) * 2f - 1f, ny = o.g / 255f * 2f - 1f;
                float l2 = nx * nx + ny * ny;
                if (l2 > 0.96f) { float inv = Mathf.Sqrt(0.96f / l2); nx *= inv; ny *= inv; l2 = 0.96f; }
                float nz = Mathf.Sqrt(1f - l2);
                px[i] = new Color32((byte)((nx * 0.5f + 0.5f) * 255f), (byte)((ny * 0.5f + 0.5f) * 255f), (byte)((nz * 0.5f + 0.5f) * 255f), 255);
            }
            return px;
        }

        private static Texture2D Upload(Texture2D tex, Color32[] px, int w, int h, string name, Texture like)
        {
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { name = name, filterMode = FilterMode.Bilinear };
                tex.wrapMode = like != null ? like.wrapMode : TextureWrapMode.Repeat;
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        // ---------------- 毎フレーム ----------------

        private readonly List<MeshPart> _allParts = new List<MeshPart>();

        /// <summary>
        /// 濃さは全部の服をまとめて計算する (インナー上下の境目で途切れないように、
        /// 全体の大きさの補正と境目の頂点の平均を共通にする)。描くのは服 (マテリアル) ごと。
        /// </summary>
        private float _poseSig = float.NaN;
        private readonly List<Transform> _sigBones = new List<Transform>();

        /// <summary>主要な関節 (cf_j_*。胸・お尻・スカート・髪などの揺れ物は除く) の回転から作る、ポーズの指紋。</summary>
        private float PoseSignature()
        {
            if (_sigBones.Count == 0)
            {
                var set = new HashSet<Transform>();
                foreach (var t in _targets.Values)
                    foreach (var p in t.Parts)
                        if (p.Renderer != null && p.Renderer.bones != null)
                            foreach (var b in p.Renderer.bones)
                            {
                                if (b == null || !b.name.StartsWith("cf_j_")) continue;
                                var n = b.name.ToLowerInvariant();
                                if (n.Contains("bust") || n.Contains("siri") || n.Contains("sk_") || n.Contains("hair") || n.Contains("tang")) continue;
                                set.Add(b);
                            }
                _sigBones.AddRange(set);
            }
            float sum = 0f; int i = 0;
            foreach (var b in _sigBones)
            {
                if (b == null) continue;
                var q = b.localRotation;
                float w = 1f + (i++ % 17) * 0.13f;
                sum += (q.x * 1.3f + q.y * 2.7f + q.z * 3.1f + q.w * 0.7f) * w;
            }
            return sum;
        }

        private void UpdateAllFrames(bool force)
        {
            if (_targets.Count == 0) return;
            // 主要な関節が動いていなければ計算しない (静止中の負荷をほぼゼロに)
            float sig = PoseSignature();
            bool moved = float.IsNaN(_poseSig) || Mathf.Abs(sig - _poseSig) > 1e-5f;
            if (!moved && !force) return;
            bool anyVisible = false, anyPending = false;
            _allParts.Clear();
            foreach (var t in _targets.Values)
            {
                if (t.Failed || t.Output == null || t.FullTex == null || t.BaseTex == null) continue;
                _allParts.AddRange(t.Parts);
                if (t.Material.GetTexture(NormalProp) != t.Output) anyPending = true;
                foreach (var p in t.Parts) if (p.Renderer != null && p.Renderer.isVisible) { anyVisible = true; break; }
            }
            if (_allParts.Count == 0 || (!anyVisible && !anyPending)) return;
            _poseSig = sig;

            try { Realtime.UpdateAmounts(_allParts, Settings); }
            catch (Exception e)
            {
                foreach (var t in _targets.Values) t.Failed = true;
                WrinklePlugin.Log.LogError("しわの濃さの計算に失敗: " + e);
                WrinklePlugin.FileLog("しわの濃さの計算に失敗: " + e);
                return;
            }
            foreach (var t in _targets.Values) DrawTarget(t);
            if (WrinklePlugin.DumpDebug.Value) DumpAll();
        }

        private void DrawTarget(Target t)
        {
            if (t.Failed || t.Output == null || t.FullTex == null || t.BaseTex == null) return;
            try
            {
                int drawn = Realtime.Draw(t.Output, t.BaseTex, t.FullTex, t.Parts);
                if (t.Material.GetTexture(NormalProp) != t.Output) t.Material.SetTexture(NormalProp, t.Output);
                if (!t.Material.IsKeywordEnabled("_NORMALMAP")) t.Material.EnableKeyword("_NORMALMAP");
                t.Applied = true;

                if (!t.Logged || WrinklePlugin.DumpDebug.Value)
                {
                    t.Logged = true;
                    int maxA = 0; long sumA = 0, n = 0;
                    foreach (var p in t.Parts) foreach (var c in p.Colors) { if (c.a > maxA) maxA = c.a; sumA += c.a; n++; }
                    WrinklePlugin.FileLog($"{t.Material.name} ({t.Material.shader.name}): 頂点 {n}, しわの濃さ 最大 {maxA / 255f:F2} 平均 {(n > 0 ? sumA / (255f * n) : 0f):F3}, 描いた三角形 {drawn}, 出力 {t.Output.width}x{t.Output.height}");
                }
            }
            catch (Exception e)
            {
                t.Failed = true; // 毎フレームのログ連発を防ぐ
                WrinklePlugin.Log.LogError($"しわの更新に失敗 ({t.Material.name}): {e}");
                WrinklePlugin.FileLog($"しわの更新に失敗 ({t.Material.name}): {e}");
            }
        }

        private void DumpAll()
        {
            WrinklePlugin.DumpDebug.Value = false;
            try
            {
                var dir = System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "UserData"), "ClothWrinkle_debug");
                System.IO.Directory.CreateDirectory(dir);
                int i = 0;
                foreach (var t in _targets.Values)
                {
                    var name = i++ + "_" + string.Join("_", t.Material.name.Split(System.IO.Path.GetInvalidFileNameChars()));
                    if (t.BaseTex != null) Realtime.Dump(t.BaseTex, System.IO.Path.Combine(dir, name + "_base.png"));
                    if (t.FullTex != null) Realtime.Dump(t.FullTex, System.IO.Path.Combine(dir, name + "_full.png"));
                    if (t.Output != null) Realtime.Dump(t.Output, System.IO.Path.Combine(dir, name + "_result.png"));
                    var cur = t.Material.GetTexture(NormalProp);
                    WrinklePlugin.FileLog($"保存: {name}  今の _NormalMap = {(cur == null ? "null" : cur.name)} (結果と同じ: {cur == t.Output})");
                }
            }
            catch (Exception e) { WrinklePlugin.FileLog("画像の保存に失敗: " + e); }
        }

        // ---------------- 復元 ----------------

        private void Restore(Target t)
        {
            if (t.Material != null && t.Output != null && t.Material.GetTexture(NormalProp) == t.Output)
            {
                t.Material.SetTexture(NormalProp, t.OrigNormal);
                if (!t.KeywordWasOn) t.Material.DisableKeyword("_NORMALMAP");
            }
            if (t.Output != null) { t.Output.Release(); Destroy(t.Output); }
            if (t.BaseTex != null) Destroy(t.BaseTex);
            if (t.FullTex != null) Destroy(t.FullTex);
            t.Output = null; t.BaseTex = null; t.FullTex = null;
            foreach (var p in t.Parts)
            {
                if (p.Baked != null) Destroy(p.Baked);
                if (p.UvMesh != null) Destroy(p.UvMesh);
                p.Baked = null; p.UvMesh = null;
            }
        }

        private void RestoreAll()
        {
            _sigBones.Clear();
            _poseSig = float.NaN;
            foreach (var t in _targets.Values) Restore(t);
            _targets.Clear();
            _layoutKey = "";
            HasReference = false;
        }

        private float BodyHeight()
        {
            try
            {
                var r = ChaControl.rendBody as SkinnedMeshRenderer;
                if (r != null && r.sharedMesh != null)
                {
                    float y = r.sharedMesh.bounds.size.y;
                    if (y > 0.01f) return y;
                }
            }
            catch { }
            return 1.5f;
        }
    }
}
