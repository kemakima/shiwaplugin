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
    /// 服のマテリアルごとに、横じわ模様 (最大の濃さ) を1回だけ作り (v0.5 から別スレッド)、
    /// 毎フレームは頂点ごとの濃さだけを計算して GPU で法線マップ (と、KK 系シェーダーなら描き込みマスク) に重ねる。
    /// </summary>
    public class WrinkleController : CharaCustomFunctionController
    {
        private const string NormalProp = "_NormalMap";
        private const string DetailProp = "_DetailMask";

        public WrinkleSettings Settings { get; private set; } = new WrinkleSettings();
        public bool HasReference { get; private set; }

        /// <summary>書き換える1枚のテクスチャ (法線マップ or 描き込みマスク)。</summary>
        private class Channel
        {
            public string Prop;
            public bool IsNormal;
            public Texture Orig;
            public bool Read;              // Orig を読み込み済み
            public Color32[] OrigPx;
            public int W, H;
            public Texture2D BaseTex;      // 元の画像 (法線は RGB に変換)
            public Texture2D Full1, Full2; // しわ最大 (1層目 / 2層目 = 深い曲げ。無ければ null)
            public RenderTexture Output;
            public bool KeywordWasOn;
            public bool Applied;           // 一度でもマテリアルに入れたか
        }

        private class Target
        {
            public Material Material;
            public List<MeshPart> Parts = new List<MeshPart>();
            public TexelBinding Binding;
            public float[] Pattern;        // しわの向き (テンソル)
            public string PatternKey;      // 今の画像を作った設定
            public string JobKey;          // 計算中の設定
            public PatternJob Job;
            public bool ToonCapable;       // KK 系シェーダー (_DetailMask の G = 描き込みの影、B = 線)
            public bool LineEnabled;       // _DetailBLineG がオン (B チャンネルが線として使われる)
            public Channel Normal = new Channel { Prop = NormalProp, IsNormal = true };
            public Channel Detail;         // アニメ調 (使わないときは null)
            public bool Failed;
            public bool Logged;
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
            if (before.PatternKey() != Settings.PatternKey()) MarkDirty(pattern: true, delay: 0.25f);
            else _frame = true; // 濃さだけの変更は次のフレームで反映
        }

        public void ForceUpdate()
        {
            if (Settings.Enabled) _frame = true;
        }

        /// <summary>
        /// 今のポーズを「しわ無し」の基準にする (false で自動の基準 = 体型込みの直立姿勢に戻す)。
        /// v0.5 から、基準にしたポーズ (骨の回転) をシーン・キャラカードに保存し、読み込み時や服の変更後に作り直す。
        /// </summary>
        public void SetReference(bool on)
        {
            if (!Settings.Enabled) { HasReference = false; return; }
            if (on == HasReference) return;
            if (on)
            {
                _refPose = CapturePose();
                ApplyRefPose(useCurrent: true);
            }
            else
            {
                _refPose = null;
                HasReference = false;
                foreach (var t in _targets.Values)
                    foreach (var p in t.Parts)
                        if (p.TriL1 != null) Realtime.SetReference(p, p.AutoRef);
            }
            _frame = true;
        }

        /// <summary>「基準にする」ポーズ: 骨の名前 → ローカル回転。null = 自動の基準。</summary>
        private Dictionary<string, Quaternion> _refPose;

        private IEnumerable<Transform> AllBones()
        {
            var set = new HashSet<Transform>();
            foreach (var t in _targets.Values)
                foreach (var p in t.Parts)
                    if (p.Renderer != null && p.Renderer.bones != null)
                        foreach (var b in p.Renderer.bones)
                            if (b != null && set.Add(b)) yield return b;
        }

        private Dictionary<string, Quaternion> CapturePose()
        {
            var d = new Dictionary<string, Quaternion>();
            foreach (var b in AllBones()) d[b.name] = b.localRotation;
            return d;
        }

        /// <summary>
        /// 基準のポーズで BakeMesh して、全パーツの基準にする。
        /// useCurrent = true なら今の形をそのまま使う (今のポーズ = 基準のポーズ のとき)。
        /// </summary>
        private void ApplyRefPose(bool useCurrent)
        {
            if (_refPose == null) return;
            var bones = new List<Transform>(AllBones());
            var saved = new Quaternion[bones.Count];
            for (int i = 0; i < bones.Count; i++) saved[i] = bones[i].localRotation;
            int ok = 0, total = 0;
            try
            {
                if (!useCurrent)
                    foreach (var b in bones)
                        if (_refPose.TryGetValue(b.name, out var q)) b.localRotation = q;
                foreach (var t in _targets.Values)
                    foreach (var p in t.Parts)
                    {
                        total++;
                        try
                        {
                            if (p.Baked == null) p.Baked = new Mesh();
                            p.Renderer.BakeMesh(p.Baked);
                            var v = p.Baked.vertices;
                            if (v.Length == p.Rest.Length && p.TriL1 != null) { Realtime.SetReference(p, v); ok++; }
                        }
                        catch (Exception e) { WrinklePlugin.Log.LogWarning("基準ポーズを記録できません: " + e.Message); }
                    }
            }
            finally
            {
                if (!useCurrent) for (int i = 0; i < bones.Count; i++) bones[i].localRotation = saved[i];
            }
            HasReference = ok > 0;
            if (!useCurrent) WrinklePlugin.FileLog($"保存されていた基準のポーズを使用: {ok}/{total} パーツ");
        }

        private static string EncodePose(Dictionary<string, Quaternion> pose)
        {
            var sb = new StringBuilder();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var kv in pose)
                sb.Append(kv.Key).Append(':').Append(kv.Value.x.ToString("R", ci)).Append(',').Append(kv.Value.y.ToString("R", ci))
                  .Append(',').Append(kv.Value.z.ToString("R", ci)).Append(',').Append(kv.Value.w.ToString("R", ci)).Append(';');
            return sb.ToString();
        }

        private static Dictionary<string, Quaternion> DecodePose(object data)
        {
            var str = data as string;
            if (string.IsNullOrEmpty(str)) return null;
            var d = new Dictionary<string, Quaternion>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var item in str.Split(';'))
            {
                int c = item.LastIndexOf(':');
                if (c <= 0) continue;
                var f = item.Substring(c + 1).Split(',');
                if (f.Length != 4) continue;
                try { d[item.Substring(0, c)] = new Quaternion(float.Parse(f[0], ci), float.Parse(f[1], ci), float.Parse(f[2], ci), float.Parse(f[3], ci)); }
                catch { }
            }
            return d.Count > 0 ? d : null;
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
            if (Settings.IsDefault() && _refPose == null) { SetExtendedData(null); return; }
            var pd = Settings.Save();
            if (_refPose != null) pd.data["RefPose"] = EncodePose(_refPose);
            SetExtendedData(pd);
        }

        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            if (maintainState) return;
            RestoreAll();
            var pd = GetExtendedData();
            Settings = WrinkleSettings.Load(pd);
            _refPose = null;
            if (pd?.data != null && pd.data.TryGetValue("RefPose", out var rp)) _refPose = DecodePose(rp);
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
                    foreach (var t in _targets.Values) StartPattern(t);
                }
                catch (Exception e) { WrinklePlugin.Log.LogError("しわ模様の作成に失敗: " + e); WrinklePlugin.FileLog("しわ模様の作成に失敗: " + e); }
                _frame = true;
            }

            PollJobs();

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
            foreach (var t in _targets.Values)
            {
                if (t.Material == null || t.Job != null) continue;
                foreach (var ch in Channels(t))
                {
                    if (!ch.Applied || ch.Output == null) continue;
                    var cur = t.Material.GetTexture(ch.Prop);
                    if (cur == ch.Output) continue;
                    if (cur == ch.Orig) _frame = true; // 元の画像に戻された → 次の描画で入れ直すだけ
                    else { t.PatternKey = null; MarkDirty(pattern: true, delay: 0.1f); } // 別の画像に差し替えられた → それを元に作り直す
                }
            }
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
                    t.ToonCapable = IsToonMaterial(t.Material);
                    t.LineEnabled = t.ToonCapable && t.Material.HasProperty("_DetailBLineG") && t.Material.GetFloat("_DetailBLineG") > 0.5f;
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
            // 「今のポーズを基準にする」で保存されたポーズがあれば、それで基準を作り直す
            if (_refPose != null)
            {
                try { ApplyRefPose(useCurrent: false); }
                catch (Exception e) { WrinklePlugin.FileLog("基準のポーズの適用に失敗: " + e.Message); }
            }
        }

        /// <summary>
        /// KK 系のトゥーンシェーダー (Koikano/main_clothes_*、Shader Forge/main_*) か。
        /// これらの _DetailMask は G = 描き込みの影、B = 描き込みの線 として使われる。
        /// Az/Standard 系の _DetailMask (ディテール用のマスク) は意味が違うので対象外。
        /// </summary>
        private static bool IsToonMaterial(Material m)
        {
            if (m == null || m.shader == null) return false;
            if (!m.HasProperty(DetailProp) || !m.HasProperty("_LineMask") || !m.HasProperty("_ShadowExtend")) return false;
            if (m.HasProperty("_AlbedoMapDetail") || m.HasProperty("_DetailAlbedoMap") || m.shader.name.StartsWith("Az/")) return false;
            // 描き込みマスクのタイリングがずれていると、UV の位置に描いても合わない
            var sc = m.GetTextureScale(DetailProp); var of = m.GetTextureOffset(DetailProp);
            return Mathf.Abs(sc.x - 1f) < 1e-3f && Mathf.Abs(sc.y - 1f) < 1e-3f && Mathf.Abs(of.x) < 1e-3f && Mathf.Abs(of.y) < 1e-3f;
        }

        private static IEnumerable<Channel> Channels(Target t)
        {
            yield return t.Normal;
            if (t.Detail != null) yield return t.Detail;
        }

        /// <summary>
        /// しわ模様の画像作りを始める (計算は別スレッド)。元の画像の読み出しだけここ (メインスレッド) で行う。
        /// 設定も元の画像も変わっていなければ何もしない。
        /// </summary>
        private void StartPattern(Target t)
        {
            if (t.Binding == null || t.Binding.Count == 0) return;
            bool toon = Settings.UseToon && t.ToonCapable;
            if (!toon && t.Detail != null) { RestoreChannel(t, t.Detail); t.Detail = null; }
            if (toon && t.Detail == null) t.Detail = new Channel { Prop = DetailProp };

            bool nChanged = PrepareChannel(t, t.Normal);
            bool dChanged = toon && PrepareChannel(t, t.Detail);
            var key = toon ? Settings.PatternKey() + (t.LineEnabled ? "|toonL" : "|toon") : Settings.NormalKey();
            bool ready = t.Normal.Full1 != null && (!toon || t.Detail.Full1 != null);
            if (!nChanged && !dChanged && ((ready && t.PatternKey == key && t.Job == null) || (t.Job != null && t.JobKey == key))) return;

            var s = Settings.Clone();
            if (!t.LineEnabled) s.ToonLine = 0f; // 線が出ないマテリアル: B チャンネル (リムライトのマスクにも使われる) は触らない
            var job = new PatternJob
            {
                Binding = t.Binding, Tensor = t.Pattern, Settings = s, Key = key,
                NormalOrig = t.Normal.OrigPx, NW = t.Normal.W, NH = t.Normal.H, NormalBase = nChanged || t.Normal.BaseTex == null,
                Toon = toon,
                DetailOrig = toon ? t.Detail.OrigPx : null, DW = toon ? t.Detail.W : 0, DH = toon ? t.Detail.H : 0,
                DetailBase = toon && (dChanged || t.Detail.BaseTex == null),
            };
            t.Job = job;
            t.JobKey = key;
            job.Start();
        }

        /// <summary>元の画像が変わっていたら読み直す。変わったら true。</summary>
        private bool PrepareChannel(Target t, Channel ch)
        {
            bool changed = false;
            var cur = t.Material.GetTexture(ch.Prop);
            if (!ch.Read || (cur != ch.Output && cur != ch.Orig))
            {
                // 初回、またはゲームや他のプラグインが別の画像に差し替えた → それを元にする
                ch.Orig = cur;
                ch.Read = false;
                ch.Applied = false;
                if (ch.IsNormal) ch.KeywordWasOn = t.Material.IsKeywordEnabled("_NORMALMAP");
            }
            OutputSize(ch.Orig != null ? ch.Orig : t.Material.mainTexture, out int w, out int h);
            if (!ch.Read || ch.W != w || ch.H != h)
            {
                ch.W = w; ch.H = h;
                ch.OrigPx = ch.Orig != null ? WrinkleBaker.ReadLinear(ch.Orig, w, h) : null;
                ch.Read = true;
                changed = true;
            }
            return changed;
        }

        /// <summary>別スレッドの計算が終わっていたら、画像をアップロードして使い始める。</summary>
        private void PollJobs()
        {
            foreach (var t in _targets.Values)
            {
                var j = t.Job;
                if (j == null || !j.Done) continue;
                t.Job = null;
                t.JobKey = null;
                if (j.Error != null)
                {
                    t.Failed = true;
                    WrinklePlugin.Log.LogError($"しわ模様の作成に失敗 ({t.Material.name}): {j.Error}");
                    WrinklePlugin.FileLog($"しわ模様の作成に失敗 ({t.Material.name}): {j.Error}");
                    continue;
                }
                // 計算中に元の画像の大きさが変わった → 作り直し
                if (j.NW != t.Normal.W || j.NH != t.Normal.H || (j.Toon && (t.Detail == null || j.DW != t.Detail.W || j.DH != t.Detail.H)))
                { MarkDirty(pattern: true); continue; }
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    UploadChannel(t.Normal, j.NormalBasePx, j.NormalFull1, j.NormalFull2, "normal");
                    if (j.Toon && t.Detail != null) UploadChannel(t.Detail, j.DetailBasePx, j.DetailFull1, j.DetailFull2, "detail");
                    foreach (var p in t.Parts)
                        if (p.UvVerts == null || p.UvW != t.Normal.W || p.UvH != t.Normal.H)
                            Realtime.BuildUvMesh(p, t.Normal.W, t.Normal.H, 1.5f);
                    t.PatternKey = j.Key;
                    t.Failed = false;
                    t.Logged = false;
                    WrinklePlugin.FileLog($"{t.Material.name}: しわ模様を作成 {t.Normal.W}x{t.Normal.H} (計算 {j.Ms}ms 別スレッド, 反映 {sw.ElapsedMilliseconds}ms), 有効テクセル {t.Binding.Count}" +
                                          (j.NormalFull2 != null ? ", 深い曲げの層あり" : "") + (j.Toon ? $", アニメ調 {t.Detail.W}x{t.Detail.H}" : ""));
                    if (j.Toon) LogToon(t);
                }
                catch (Exception e)
                {
                    t.Failed = true;
                    WrinklePlugin.FileLog($"しわ模様の反映に失敗 ({t.Material.name}): {e}");
                }
                _frame = true;
            }
        }

        private void UploadChannel(Channel ch, Color32[] basePx, Color32[] full1, Color32[] full2, string tag)
        {
            int w = ch.W, h = ch.H;
            if (basePx != null) ch.BaseTex = Upload(ch.BaseTex, basePx, w, h, "ClothWrinkle_" + tag + "_base", ch.Orig);
            ch.Full1 = Upload(ch.Full1, full1, w, h, "ClothWrinkle_" + tag + "_full", ch.Orig);
            if (full2 != null) ch.Full2 = Upload(ch.Full2, full2, w, h, "ClothWrinkle_" + tag + "_deep", ch.Orig);
            else if (ch.Full2 != null) { Destroy(ch.Full2); ch.Full2 = null; }

            if (ch.Output == null || ch.Output.width != w || ch.Output.height != h)
            {
                if (ch.Output != null)
                {
                    if (ch.Applied) ch.Applied = false;
                    ch.Output.Release(); Destroy(ch.Output);
                }
                ch.Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "ClothWrinkle_" + tag, useMipMap = true, autoGenerateMips = true, anisoLevel = 4,
                    wrapMode = ch.Orig != null ? ch.Orig.wrapMode : TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear
                };
                ch.Output.Create();
            }
        }

        private void LogToon(Target t)
        {
            var m = t.Material;
            float ext = m.HasProperty("_ShadowExtend") ? m.GetFloat("_ShadowExtend") : -1f;
            WrinklePlugin.FileLog($"{m.name}: アニメ調 線 {(t.LineEnabled ? "あり" : "なし (_DetailBLineG がオフ)")}, " +
                                  $"影の効き _ShadowExtend={ext:F2}{(ext <= 0.01f ? " (0 なので影は出ない)" : "")}, 線の濃さ _linewidthG={Shader.GetGlobalFloat("_linewidthG"):F2}");
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

        private static bool Ready(Channel ch) => ch != null && ch.Output != null && ch.BaseTex != null && ch.Full1 != null;

        // 調整用の計測 (Log strain stats がオンのとき 5 秒ごとにログ)
        private int _statN;
        private double _statAmount, _statDraw;
        private float _statNext;

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
                if (t.Failed || !Ready(t.Normal)) continue;
                _allParts.AddRange(t.Parts);
                foreach (var ch in Channels(t)) if (Ready(ch) && t.Material.GetTexture(ch.Prop) != ch.Output) anyPending = true;
                foreach (var p in t.Parts) if (p.Renderer != null && p.Renderer.isVisible) { anyVisible = true; break; }
            }
            if (_allParts.Count == 0 || (!anyVisible && !anyPending)) return;
            _poseSig = sig;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Realtime.UpdateAmounts(_allParts, Settings);
                bool deep = Settings.DeepFolds > 0.001f, upload = Realtime.UseMeshPath();
                foreach (var p in _allParts) Realtime.FillUvColors(p, deep, upload);
            }
            catch (Exception e)
            {
                foreach (var t in _targets.Values) t.Failed = true;
                WrinklePlugin.Log.LogError("しわの濃さの計算に失敗: " + e);
                WrinklePlugin.FileLog("しわの濃さの計算に失敗: " + e);
                return;
            }
            double msAmount = sw.Elapsed.TotalMilliseconds;
            foreach (var t in _targets.Values) DrawTarget(t);
            if (WrinklePlugin.DumpDebug.Value) DumpAll();

            if (WrinklePlugin.DebugLog.Value)
            {
                _statN++; _statAmount += msAmount; _statDraw += sw.Elapsed.TotalMilliseconds - msAmount;
                if (Time.unscaledTime >= _statNext)
                {
                    _statNext = Time.unscaledTime + 5f;
                    WrinklePlugin.FileLog($"更新 {_statN} 回 / 平均: 濃さの計算 {_statAmount / _statN:F2}ms, 描画 {_statDraw / _statN:F2}ms ({(Realtime.UseMeshPath() ? "メッシュ一括" : "GL")})");
                    _statN = 0; _statAmount = _statDraw = 0;
                }
            }
        }

        private void DrawTarget(Target t)
        {
            if (t.Failed || !Ready(t.Normal)) return;
            try
            {
                int drawn = Realtime.Draw(t.Normal.Output, t.Normal.BaseTex, t.Normal.Full1, t.Normal.Full2, t.Parts, Realtime.MaskRGB);
                Apply(t, t.Normal);
                if (Ready(t.Detail))
                {
                    Realtime.Draw(t.Detail.Output, t.Detail.BaseTex, t.Detail.Full1, t.Detail.Full2, t.Parts, Realtime.MaskGB);
                    Apply(t, t.Detail);
                }

                if (!t.Logged || WrinklePlugin.DumpDebug.Value)
                {
                    t.Logged = true;
                    int maxA = 0, maxA2 = 0; long sumA = 0, n = 0;
                    foreach (var p in t.Parts)
                    {
                        foreach (var c in p.Colors) { if (c.a > maxA) maxA = c.a; sumA += c.a; n++; }
                        if (p.MaxA2 > maxA2) maxA2 = p.MaxA2;
                    }
                    WrinklePlugin.FileLog($"{t.Material.name} ({t.Material.shader.name}): 頂点 {n}, しわの濃さ 最大 {maxA / 255f:F2} 平均 {(n > 0 ? sumA / (255f * n) : 0f):F3}, 深い曲げ 最大 {maxA2 / 255f:F2}, 描いた三角形 {drawn}, 出力 {t.Normal.Output.width}x{t.Normal.Output.height}" +
                                          (Ready(t.Detail) ? " + アニメ調" : ""));
                }
            }
            catch (Exception e)
            {
                t.Failed = true; // 毎フレームのログ連発を防ぐ
                WrinklePlugin.Log.LogError($"しわの更新に失敗 ({t.Material.name}): {e}");
                WrinklePlugin.FileLog($"しわの更新に失敗 ({t.Material.name}): {e}");
            }
        }

        private static void Apply(Target t, Channel ch)
        {
            if (t.Material.GetTexture(ch.Prop) != ch.Output) t.Material.SetTexture(ch.Prop, ch.Output);
            if (ch.IsNormal && !t.Material.IsKeywordEnabled("_NORMALMAP")) t.Material.EnableKeyword("_NORMALMAP");
            ch.Applied = true;
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
                    foreach (var ch in Channels(t))
                    {
                        var tag = ch.IsNormal ? "_normal" : "_detail";
                        if (ch.BaseTex != null) Realtime.Dump(ch.BaseTex, System.IO.Path.Combine(dir, name + tag + "_base.png"));
                        if (ch.Full1 != null) Realtime.Dump(ch.Full1, System.IO.Path.Combine(dir, name + tag + "_full.png"));
                        if (ch.Full2 != null) Realtime.Dump(ch.Full2, System.IO.Path.Combine(dir, name + tag + "_deep.png"));
                        if (ch.Output != null) Realtime.Dump(ch.Output, System.IO.Path.Combine(dir, name + tag + "_result.png"));
                        var cur = t.Material.GetTexture(ch.Prop);
                        WrinklePlugin.FileLog($"保存: {name}{tag}  今の {ch.Prop} = {(cur == null ? "null" : cur.name)} (結果と同じ: {cur == ch.Output})");
                    }
                }
            }
            catch (Exception e) { WrinklePlugin.FileLog("画像の保存に失敗: " + e); }
        }

        // ---------------- 復元 ----------------

        private void RestoreChannel(Target t, Channel ch)
        {
            if (ch == null) return;
            if (t.Material != null && ch.Output != null && t.Material.GetTexture(ch.Prop) == ch.Output)
            {
                t.Material.SetTexture(ch.Prop, ch.Orig);
                if (ch.IsNormal && !ch.KeywordWasOn) t.Material.DisableKeyword("_NORMALMAP");
            }
            if (ch.Output != null) { ch.Output.Release(); Destroy(ch.Output); }
            if (ch.BaseTex != null) Destroy(ch.BaseTex);
            if (ch.Full1 != null) Destroy(ch.Full1);
            if (ch.Full2 != null) Destroy(ch.Full2);
            ch.Output = null; ch.BaseTex = null; ch.Full1 = null; ch.Full2 = null;
            ch.Applied = false; ch.Read = false; ch.OrigPx = null;
        }

        private void Restore(Target t)
        {
            t.Job = null; // 計算中のものは捨てる
            t.JobKey = null;
            RestoreChannel(t, t.Normal);
            RestoreChannel(t, t.Detail);
            t.Detail = null;
            t.PatternKey = null;
            foreach (var p in t.Parts)
            {
                if (p.Baked != null) Destroy(p.Baked);
                p.Baked = null;
                Realtime.DestroyUvMeshes(p);
                p.UvVerts = null;
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
