using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>1つのマテリアルを使うメッシュ部分 (レンダラー + サブメッシュ)。</summary>
    internal class MeshPart
    {
        public SkinnedMeshRenderer Renderer;
        public int SubMesh;
        public Vector3[] Rest;      // sharedMesh の頂点 (バインドポーズ)
        public Vector3[] Reference; // 「基準にする」で記録したポーズ (無ければ null)
        public int[] Tris;
        public Vector2[] Uv;
        public float[] AxisCoord;   // 頂点ごとの「体の軸に沿った座標」(胴体・脚 = 高さ、腕 = 肩からの長さ)。しま模様の位相に使う
        public Vector3[] Axis;      // 頂点ごとの「体・手足の長さ方向」(元の形の空間)。横じわに揃えるとき使う
        public int[] Weld;          // 頂点 → 溶接ID (UVの継ぎ目で分かれた頂点を1つにする)
        public int WeldOffset;      // ターゲット全体での溶接IDの開始番号
        public int WeldCount;
        public Mesh Baked;          // BakeMesh 用 (使い回し)

        // ---- リアルタイム用 (Realtime.cs) ----
        public float[] TriL1, TriBx, TriBy, TriArea; // 元の形の三角形を2Dにした値
        public float[] TriAx, TriAy;                 // 体の軸 (しわと直角の向き) を三角形の2D座標で表したもの
        public Vector3[] AutoRef;                    // 体型だけ反映した直立 (バインド) 姿勢の形
        public float[] RefB;                         // 基準の計量 (b11,b12,b22) ×三角形。null = 元の形
        public double RefAreaSum;
        public Color32[] Colors;                     // 頂点ごとのしわの濃さ (アルファ)

        // ---- UV 展開メッシュ (v0.5) ----
        public Vector3[] UvVerts;                    // UV 空間の位置 (三角形ごとに独立した頂点 + 島の外周のすそ)
        public int[] UvTris;                         // すそ (外周) を先に、本体を後に並べる
        public int[] UvSrc;                          // UV 頂点 → 元の頂点番号 (濃さの参照先)
        public int UvW, UvH;                         // すその幅を決めた出力サイズ
        public Mesh UvMesh1, UvMesh2;                // 1層目 (通常) / 2層目 (深い曲げ) の濃さを頂点色に持つ
        public Color32[] UvCol1, UvCol2;
        public int MaxA1, MaxA2;                     // 今のフレームの最大の濃さ (0..255)
        public int[] GWeld;                          // キャラ全体で共通の溶接番号 (服をまたぐ)
        public int GWeldTotal;
        public List<Vector3> CurBuf = new List<Vector3>();
    }

    /// <summary>テクセルごとに「どの頂点をどの重みで補間するか」。ポーズが変わっても使い回す。</summary>
    internal class TexelBinding
    {
        public int Res;
        public int Count;
        public int[] Texel;
        public int[] V0, V1, V2;    // 溶接ID (全体)
        public float[] W0, W1, W2;
        public float[] Coord;       // 体の軸に沿った座標 (正規化)
        public Vector3[] Pos;       // 元の形での位置 (体の高さで正規化)。しわ模様のノイズ用
    }

    internal static class WrinkleBaker
    {
        // ---------------- 準備 (服が変わったときだけ) ----------------

        public static void PrepareParts(List<MeshPart> parts)
        {
            int offset = 0;
            foreach (var p in parts)
            {
                var mesh = p.Renderer.sharedMesh;
                p.Rest = mesh.vertices;
                p.Tris = mesh.GetTriangles(Mathf.Clamp(p.SubMesh, 0, mesh.subMeshCount - 1));
                p.Uv = mesh.uv;
                p.Weld = BuildWeld(p.Rest, out p.WeldCount);
                try { p.Axis = BuildAxis(p.Renderer, mesh, out p.AxisCoord); }
                catch (Exception e) { p.Axis = null; p.AxisCoord = null; WrinklePlugin.Log.LogDebug("ボーンの向きを読めません: " + e.Message); }
                p.WeldOffset = offset;
                offset += p.WeldCount;
            }
        }

        /// <summary>
        /// 頂点ごとの長さ方向。胴体・脚は上下 (Y)。腕・手は、いちばん重みの大きいボーンから
        /// その子ボーンへの向き (バインドポーズ) を使う。
        /// </summary>
        private static Vector3[] BuildAxis(SkinnedMeshRenderer r, Mesh mesh, out float[] coord)
        {
            var bones = r.bones;
            var bind = mesh.bindposes;
            var weights = mesh.boneWeights;
            int nv = mesh.vertexCount;
            var axis = new Vector3[nv];
            var verts = mesh.vertices;
            coord = new float[nv];
            for (int i = 0; i < nv; i++) coord[i] = verts[i].y;
            if (bones == null || bind == null || weights == null || weights.Length != nv || bind.Length != bones.Length)
            {
                for (int i = 0; i < nv; i++) axis[i] = Vector3.up;
                return axis;
            }

            var pos = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++) pos[i] = bind[i].inverse.MultiplyPoint3x4(Vector3.zero);

            var boneAxis = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                boneAxis[i] = Vector3.up;
                var b = bones[i];
                if (b == null || !IsArm(b.name)) continue;
                // 子ボーン (このレンダラーの骨の中で、いちばん遠い直接の子) への向き
                Vector3 best = Vector3.zero; float bestLen = 0f;
                for (int j = 0; j < bones.Length; j++)
                {
                    if (bones[j] == null || bones[j].parent != b) continue;
                    var dv = pos[j] - pos[i];
                    if (dv.sqrMagnitude > bestLen) { bestLen = dv.sqrMagnitude; best = dv; }
                }
                if (bestLen > 1e-8f) boneAxis[i] = best.normalized;
                else if (b.parent != null)
                {
                    int pi = Array.IndexOf(bones, b.parent);
                    if (pi >= 0 && (pos[i] - pos[pi]).sqrMagnitude > 1e-8f) boneAxis[i] = (pos[i] - pos[pi]).normalized;
                    else boneAxis[i] = new Vector3(Mathf.Sign(pos[i].x == 0f ? 1f : pos[i].x), 0f, 0f);
                }
                else boneAxis[i] = new Vector3(Mathf.Sign(pos[i].x == 0f ? 1f : pos[i].x), 0f, 0f);
            }

            for (int v = 0; v < nv; v++)
            {
                int bi = weights[v].boneIndex0;
                axis[v] = bi >= 0 && bi < boneAxis.Length ? boneAxis[bi] : Vector3.up;
            }

            // 腕のボーンごとの「軸に沿った座標」の起点。肩 (いちばん根元の腕ボーン) では胴体と同じ高さから始め、
            // 子ボーンでは親の軸に沿って続ける → 関節をまたいでも座標がなめらかにつながり、しまが詰まらない
            var start = new float[bones.Length];
            var done = new bool[bones.Length];
            var isArm = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++) isArm[i] = bones[i] != null && IsArm(bones[i].name);
            Func<int, float> startOf = null;
            startOf = i =>
            {
                if (done[i]) return start[i];
                done[i] = true;
                start[i] = pos[i].y;
                for (var t = bones[i].parent; t != null; t = t.parent)
                {
                    int j = Array.IndexOf(bones, t);
                    if (j < 0) continue;
                    if (isArm[j]) start[i] = startOf(j) + Vector3.Dot(boneAxis[j], pos[i] - pos[j]);
                    break;
                }
                return start[i];
            };
            for (int i = 0; i < bones.Length; i++) if (isArm[i]) startOf(i);

            for (int v = 0; v < nv; v++)
            {
                var bw = weights[v];
                float sum = 0f, wsum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int bi = k == 0 ? bw.boneIndex0 : k == 1 ? bw.boneIndex1 : k == 2 ? bw.boneIndex2 : bw.boneIndex3;
                    float w = k == 0 ? bw.weight0 : k == 1 ? bw.weight1 : k == 2 ? bw.weight2 : bw.weight3;
                    if (w <= 0f || bi < 0 || bi >= bones.Length) continue;
                    float c = isArm[bi] ? start[bi] + Vector3.Dot(boneAxis[bi], verts[v] - pos[bi]) : verts[v].y;
                    sum += c * w; wsum += w;
                }
                if (wsum > 1e-6f) coord[v] = sum / wsum;
            }
            return axis;
        }

        private static bool IsArm(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("arm") || n.Contains("elbo") || n.Contains("wrist") || n.Contains("hand") ||
                   n.Contains("shoulder") || n.Contains("hijiu") || n.Contains("finger") || n.Contains("thumb") ||
                   n.Contains("index") || n.Contains("middle") || n.Contains("ring") || n.Contains("little");
        }

        private static int[] BuildWeld(Vector3[] v, out int count)
        {
            var map = new Dictionary<long, int>(v.Length);
            var weld = new int[v.Length];
            count = 0;
            for (int i = 0; i < v.Length; i++)
            {
                long kx = (long)Mathf.Round(v[i].x * 20000f) & 0x1FFFFF;
                long ky = (long)Mathf.Round(v[i].y * 20000f) & 0x1FFFFF;
                long kz = (long)Mathf.Round(v[i].z * 20000f) & 0x1FFFFF;
                long key = (kx << 42) | (ky << 21) | kz;
                if (!map.TryGetValue(key, out var id)) { id = count++; map[key] = id; }
                weld[i] = id;
            }
            return weld;
        }

        public static TexelBinding BuildBinding(List<MeshPart> parts, int res, float heightRef)
        {
            int n = res * res;
            var v0 = new int[n]; var v1 = new int[n]; var v2 = new int[n];
            var w0 = new float[n]; var w1 = new float[n]; var w2 = new float[n];
            var pos = new Vector3[n];
            var crd = new float[n];
            var valid = new bool[n];
            float inv = 1f / Mathf.Max(0.01f, heightRef);

            foreach (var p in parts)
            {
                var uvs = p.Uv;
                if (uvs == null || uvs.Length != p.Rest.Length) continue;
                var tris = p.Tris;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    Vector2 a = uvs[i0], b = uvs[i1], c = uvs[i2];
                    var center = (a + b + c) / 3f;
                    var off = new Vector2(Mathf.Floor(center.x), Mathf.Floor(center.y));
                    a = (a - off) * res; b = (b - off) * res; c = (c - off) * res;

                    int g0 = p.WeldOffset + p.Weld[i0], g1 = p.WeldOffset + p.Weld[i1], g2 = p.WeldOffset + p.Weld[i2];
                    Vector3 pa = p.Rest[i0] * inv, pb = p.Rest[i1] * inv, pc = p.Rest[i2] * inv;
                    float ca = (p.AxisCoord != null ? p.AxisCoord[i0] : p.Rest[i0].y) * inv;
                    float cb = (p.AxisCoord != null ? p.AxisCoord[i1] : p.Rest[i1].y) * inv;
                    float cc = (p.AxisCoord != null ? p.AxisCoord[i2] : p.Rest[i2].y) * inv;

                    int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                    int maxX = Mathf.Min(res - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                    int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                    int maxY = Mathf.Min(res - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                    if (minX > maxX || minY > maxY) continue;
                    float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                    if (Mathf.Abs(area) < 1e-8f) continue;
                    float ia = 1f / area;
                    const float eps = -0.02f;

                    for (int y = minY; y <= maxY; y++)
                    {
                        float py = y + 0.5f;
                        for (int x = minX; x <= maxX; x++)
                        {
                            float px = x + 0.5f;
                            float b0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) * ia;
                            float b1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) * ia;
                            float b2 = 1f - b0 - b1;
                            if (b0 < eps || b1 < eps || b2 < eps) continue;
                            int idx = y * res + x;
                            v0[idx] = g0; v1[idx] = g1; v2[idx] = g2;
                            w0[idx] = b0; w1[idx] = b1; w2[idx] = b2;
                            pos[idx] = pa * b0 + pb * b1 + pc * b2;
                            crd[idx] = ca * b0 + cb * b1 + cc * b2;
                            valid[idx] = true;
                        }
                    }
                }
            }

            // 継ぎ目のにじみ防止: 有効テクセルを周りに4回広げる (隣の割り当てをコピー)。
            // UV の島の外周を少し広げて描く (すそ) ので、その分まで模様が続くようにする
            for (int pass = 0; pass < 4; pass++)
            {
                var nv = (bool[])valid.Clone();
                for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    int idx = y * res + x;
                    if (valid[idx]) continue;
                    int src = -1;
                    if (x > 0 && valid[idx - 1]) src = idx - 1;
                    else if (x < res - 1 && valid[idx + 1]) src = idx + 1;
                    else if (y > 0 && valid[idx - res]) src = idx - res;
                    else if (y < res - 1 && valid[idx + res]) src = idx + res;
                    if (src < 0) continue;
                    v0[idx] = v0[src]; v1[idx] = v1[src]; v2[idx] = v2[src];
                    w0[idx] = w0[src]; w1[idx] = w1[src]; w2[idx] = w2[src];
                    pos[idx] = pos[src];
                    crd[idx] = crd[src];
                    nv[idx] = true;
                }
                valid = nv;
            }

            int count = 0;
            for (int i = 0; i < n; i++) if (valid[i]) count++;
            var bnd = new TexelBinding
            {
                Res = res, Count = count, Texel = new int[count],
                V0 = new int[count], V1 = new int[count], V2 = new int[count],
                W0 = new float[count], W1 = new float[count], W2 = new float[count], Pos = new Vector3[count], Coord = new float[count]
            };
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (!valid[i]) continue;
                bnd.Texel[k] = i;
                bnd.V0[k] = v0[i]; bnd.V1[k] = v1[i]; bnd.V2[k] = v2[i];
                bnd.W0[k] = w0[i]; bnd.W1[k] = w1[i]; bnd.W2[k] = w2[i];
                bnd.Pos[k] = pos[i];
                bnd.Coord[k] = crd[i];
                k++;
            }
            return bnd;
        }

        // ---------------- 三角形の計量 ----------------

        /// <summary>三角形の辺 E1, E2 (現在) と元の2D形 (l1,0),(bx,by) から、計量 FᵀF を求める。</summary>
        internal static void Metric(Vector3 E1, Vector3 E2, float l1, float bx, float by, out float c11, out float c12, out float c22)
        {
            // F = [E1 E2] · inv([[l1, bx],[0, by]])
            var f1 = E1 / l1;
            var f2 = E1 * (-bx / (l1 * by)) + E2 / by;
            c11 = Vector3.Dot(f1, f1);
            c12 = Vector3.Dot(f1, f2);
            c22 = Vector3.Dot(f2, f2);
        }

        internal static void Add(float[] M, float[] W, int id, Vector3 d, float m, float wt)
        {
            int o = id * 6;
            M[o] += m * d.x * d.x; M[o + 1] += m * d.y * d.y; M[o + 2] += m * d.z * d.z;
            M[o + 3] += m * d.x * d.y; M[o + 4] += m * d.x * d.z; M[o + 5] += m * d.y * d.z;
            W[id] += wt;
        }

        // ---------------- しわ模様 (テクセル) ----------------

        /// <summary>
        /// しわ模様の素材 (しわ計算の解像度 Res×Res)。すべて「しわが最大に出たとき」の値。
        /// 1層目 = 通常の折り目、2層目 = 深く曲げたとき (1層目が深くなり、間に新しい折り目が増える)。
        /// </summary>
        internal class PatternLayers
        {
            public int Res;
            public float[] H1, H2;   // 高さ (0 = 平ら、負 = 折り目のくぼみ)。H2 は深い曲げ用 (無ければ null)
            public float[] L1, L2;   // アニメ調の線 0..1 (アニメ調なしなら null)
            public float[] S1, S2;   // アニメ調の影 0..1
        }

        /// <summary>
        /// 横じわの模様を作る。位相 = 体の軸に沿った座標 (Coord) × 細かさ + 低周波ノイズのゆらぎ。
        /// 位相が整数の所が折り目の谷 (アニメ調の線)、その少し上側がアニメ調の影。
        /// メインスレッド以外からも呼べる (Unity の API は使わない)。
        /// </summary>
        public static PatternLayers BuildLayers(TexelBinding b, float[] M, WrinkleSettings s, bool toon)
        {
            int res = b.Res, n = res * res;
            bool deep = s.DeepFolds > 0.001f;
            var L = new PatternLayers { Res = res, H1 = new float[n] };
            if (deep) L.H2 = new float[n];
            if (toon)
            {
                L.L1 = new float[n]; L.S1 = new float[n];
                if (deep) { L.L2 = new float[n]; L.S2 = new float[n]; }
            }

            float f = s.Detail;
            const float aniso = 4f;                          // 折り目の間隔 = 体の高さ / (細かさ × 4)
            float sharpExp = 1f + s.Sharpness * 7f;
            float deepGain = 1f + 0.35f * s.DeepFolds;       // 深く曲げると1層目の折り目も深くなる
            float lineW = 0.085f - 0.04f * s.Sharpness;      // 線の半分の幅 (折り目の間隔に対する割合)
            float shadowW = 0.42f - 0.18f * s.Sharpness;     // 影の幅
            var seed = new Vector3(s.Seed * 13.37f % 101f, s.Seed * 7.13f % 97f, s.Seed * 3.71f % 89f) + new Vector3(5.3f, 11.9f, 2.7f);
            const float TwoPi = 2f * (float)Math.PI;

            // アニメ調の2周目で使う値 (テクセルごと)
            bool[] valid = null; float[] PH1 = null, PH2 = null, G2 = null, SEG = null, SHG = null;
            if (toon) { valid = new bool[n]; PH1 = new float[n]; PH2 = new float[n]; G2 = new float[n]; SEG = new float[n]; SHG = new float[n]; }

            for (int k = 0; k < b.Count; k++)
            {
                // 体の軸が面とほぼ直角な所 (テンソルが空) は模様なし
                float w0 = b.W0[k], w1 = b.W1[k], w2 = b.W2[k];
                int o0 = b.V0[k] * 6, o1 = b.V1[k] * 6, o2 = b.V2[k] * 6;
                float tr = (M[o0] + M[o0 + 1] + M[o0 + 2]) * w0 + (M[o1] + M[o1 + 1] + M[o1 + 2]) * w1 + (M[o2] + M[o2 + 1] + M[o2 + 2]) * w2;
                if (tr < 1e-5f) continue;

                var p = b.Pos[k];
                int tx = b.Texel[k];

                // 1層目: 軸に沿ったしま (関節で向きが変わっても間隔が詰まらない)
                float qx = p.x * f + seed.x, qy = p.y * f + seed.y, qz = p.z * f + seed.z;
                float warp = Perlin.Noise(qx, qy, qz) * 1.1f + Perlin.Noise(qx * 2.1f + 5.2f, qy * 2.1f + 1.7f, qz * 2.1f + 8.4f) * 0.35f;
                float ph1 = b.Coord[k] * f * aniso + warp;
                float fold1 = Pow01(0.5f + 0.5f * (float)Math.Cos(ph1 * TwoPi), sharpExp);
                // 折り目の長さに変化を付ける
                float breakup = 0.55f + 0.45f * (Perlin.Noise(p.x * f * 0.7f + 41f, p.y * f * 0.7f + 7f, p.z * f * 0.7f + 23f) * 0.5f + 0.5f);
                L.H1[tx] = -fold1 * breakup;

                // 2層目: 1層目の折り目の間に、短い折り目が増える
                float ph2 = 0f, gate2 = 0f;
                if (deep)
                {
                    float wq = Perlin.Noise(p.x * f * 1.3f + seed.x + 17.1f, p.y * f * 1.3f + seed.y + 3.3f, p.z * f * 1.3f + seed.z + 29.7f);
                    ph2 = ph1 + 0.5f + wq * 0.12f;
                    float fold2 = Pow01(0.5f + 0.5f * (float)Math.Cos(ph2 * TwoPi), sharpExp * 1.3f);
                    float n2 = Perlin.Noise(p.x * f * 0.9f + 13f, p.y * f * 0.9f + 57f, p.z * f * 0.9f + 71f) * 0.5f + 0.5f;
                    gate2 = Smooth01((n2 - 0.3f) / 0.35f);
                    L.H2[tx] = -(fold1 * breakup * deepGain + fold2 * gate2 * s.DeepFolds * 0.8f);
                }

                if (toon)
                {
                    // 線はところどころで途切れる (先細りの短い線)。影は線より長く続く
                    float nseg = Perlin.Noise(p.x * f * 0.9f + 91f, p.y * f * 0.9f + 19f, p.z * f * 0.9f + 3f) * 0.5f + 0.5f;
                    float nsh = Perlin.Noise(p.x * f * 0.6f + 7f, p.y * f * 0.6f + 77f, p.z * f * 0.6f + 37f) * 0.5f + 0.5f;
                    valid[tx] = true;
                    PH1[tx] = ph1; PH2[tx] = ph2; G2[tx] = gate2;
                    SEG[tx] = Smooth01((nseg - 0.4f) / 0.22f);
                    SHG[tx] = Smooth01((nsh - 0.22f) / 0.3f) * breakup;
                }
            }

            if (!toon) return L;

            // 2周目: アニメ調の線と影。位相の傾き (1テクセルあたり) を見て、線の幅をテクセルに合わせてなめらかにする
            for (int k = 0; k < b.Count; k++)
            {
                int tx = b.Texel[k];
                if (!valid[tx]) continue;
                int x = tx % res, y = tx / res;
                float g1 = PhaseStep(PH1, valid, x, y, res), g2 = deep ? PhaseStep(PH2, valid, x, y, res) : 0f;
                float seg = SEG[tx], shGate = SHG[tx], gate2 = G2[tx];

                float l1 = LineProfile(PH1[tx], lineW, seg, g1) * s.ToonLine;
                float s1 = ShadowProfile(PH1[tx], shadowW, g1) * shGate * s.ToonShadow;
                L.L1[tx] = l1;
                L.S1[tx] = s1;
                if (deep)
                {
                    float l2 = LineProfile(PH2[tx], lineW * 0.8f, seg * gate2, g2) * s.ToonLine * s.DeepFolds;
                    float s2 = ShadowProfile(PH2[tx], shadowW * 0.8f, g2) * shGate * gate2 * s.ToonShadow * s.DeepFolds;
                    L.L2[tx] = Math.Max(l1, l2);
                    L.S2[tx] = Math.Min(1f, Math.Max(s1 * deepGain, s2));
                }
            }
            return L;
        }

        /// <summary>となりのテクセルとの位相の差 (= 1テクセルが位相でどれだけか)。</summary>
        private static float PhaseStep(float[] ph, bool[] valid, int x, int y, int res)
        {
            int i = y * res + x;
            float g = 0f; float c = ph[i];
            if (x > 0 && valid[i - 1]) g = Math.Max(g, Math.Abs(c - ph[i - 1]));
            if (x < res - 1 && valid[i + 1]) g = Math.Max(g, Math.Abs(c - ph[i + 1]));
            if (y > 0 && valid[i - res]) g = Math.Max(g, Math.Abs(c - ph[i - res]));
            if (y < res - 1 && valid[i + res]) g = Math.Max(g, Math.Abs(c - ph[i + res]));
            return Math.Min(g, 0.25f);
        }

        /// <summary>
        /// 折り目の谷 (位相が整数) に沿った線。seg (0..1) が小さい所ほど細くなり、0 で途切れる (先細りの線)。
        /// 縁は 1 テクセル分 (step) でぼかして、細い線が点線にならないようにする。
        /// </summary>
        private static float LineProfile(float ph, float w, float seg, float step)
        {
            if (seg <= 0f || w <= 0f) return 0f;
            float hw = w * (0.3f + 0.7f * seg);
            float d = Math.Abs(ph - (float)Math.Round(ph));
            float aa = Math.Max(step, hw * 0.35f);
            float cov = (hw - d) / aa + 0.5f;
            if (cov <= 0f) return 0f;
            if (cov > 1f) cov = 1f;
            return cov * Math.Min(1f, seg * 4f);
        }

        /// <summary>折り目の谷から上側 (位相が増える側) へ広がる影。谷の所で一番濃く、谷の線ではくっきり切れる。</summary>
        private static float ShadowProfile(float ph, float w, float step)
        {
            float d = ph - (float)Math.Floor(ph); // 0 = 谷、増えるほど上
            float v = Smooth01(1f - d / w);
            // 谷のすぐ下 (d が 1 に近い所) は影なし。境目は 1 テクセル分ぼかす
            float edge = step > 1e-5f ? Math.Min(1f, (1f - d) / step) : 1f;
            return v * edge;
        }

        internal static float Smooth01(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x * x * (3f - 2f * x);
        }

        private static float Pow01(float x, float e) => x <= 0f ? 0f : (float)Math.Pow(x, e);

        // ---------------- 法線マップ合成 ----------------

        /// <summary>
        /// しわの高さ(Res)の傾きを出力解像度に補間し、元の法線(無ければ平ら)に足して RGBA を作る。
        /// 元の法線は RGB / DXT5nm(AG) のどちらでも x = R*A, y = G で読める。出力は A=255 の RGB。
        /// </summary>
        public static Color32[] Compose(float[] h, int res, Color32[] orig, int w, int ht, float strength)
        {
            // 高さの傾き (Res 空間)
            var gx = new float[res * res];
            var gy = new float[res * res];
            float k = strength * 6f * (res / 512f);
            for (int y = 0; y < res; y++)
            {
                int ym = y > 0 ? y - 1 : y, yp = y < res - 1 ? y + 1 : y;
                for (int x = 0; x < res; x++)
                {
                    int xm = x > 0 ? x - 1 : x, xp = x < res - 1 ? x + 1 : x;
                    int i = y * res + x;
                    gx[i] = (h[y * res + xp] - h[y * res + xm]) * 0.5f * k;
                    gy[i] = (h[yp * res + x] - h[ym * res + x]) * 0.5f * k;
                }
            }

            var dst = new Color32[w * ht];
            float sx = (float)res / w, sy = (float)res / ht;
            for (int y = 0; y < ht; y++)
            {
                float my = (y + 0.5f) * sy - 0.5f;
                int y0 = my < 0 ? 0 : (int)my; if (y0 > res - 1) y0 = res - 1;
                int y1 = y0 + 1 < res ? y0 + 1 : res - 1;
                float fy = Mathf.Clamp01(my - y0);
                for (int x = 0; x < w; x++)
                {
                    float mx = (x + 0.5f) * sx - 0.5f;
                    int x0 = mx < 0 ? 0 : (int)mx; if (x0 > res - 1) x0 = res - 1;
                    int x1 = x0 + 1 < res ? x0 + 1 : res - 1;
                    float fx = Mathf.Clamp01(mx - x0);
                    int a = y0 * res + x0, bb = y0 * res + x1, c = y1 * res + x0, d = y1 * res + x1;
                    float dx = Mathf.Lerp(Mathf.Lerp(gx[a], gx[bb], fx), Mathf.Lerp(gx[c], gx[d], fx), fy);
                    float dy = Mathf.Lerp(Mathf.Lerp(gy[a], gy[bb], fx), Mathf.Lerp(gy[c], gy[d], fx), fy);

                    int i = y * w + x;
                    float ox = 0f, oy = 0f;
                    if (orig != null)
                    {
                        var o = orig[i];
                        ox = (o.r / 255f) * (o.a / 255f) * 2f - 1f;
                        oy = o.g / 255f * 2f - 1f;
                    }
                    float nx = ox - dx, ny = oy - dy;
                    float l2 = nx * nx + ny * ny;
                    if (l2 > 0.96f) { float inv = Mathf.Sqrt(0.96f / l2); nx *= inv; ny *= inv; l2 = 0.96f; }
                    float nz = Mathf.Sqrt(1f - l2);
                    dst[i] = new Color32((byte)((nx * 0.5f + 0.5f) * 255f), (byte)((ny * 0.5f + 0.5f) * 255f),
                        (byte)((nz * 0.5f + 0.5f) * 255f), 255);
                }
            }
            return dst;
        }

        /// <summary>
        /// アニメ調の線・影 (Res) を出力サイズに補間し、元の _DetailMask に重ねる。
        /// B = 線 (KK 系シェーダーの描き込み線)、G = 描き込みの影。R は元のまま。
        /// A は合成時の混ぜ具合に使うので 255 (出力の A は元の値が残る)。
        /// </summary>
        public static Color32[] ComposeDetail(float[] line, float[] shadow, int res, Color32[] orig, int w, int ht)
        {
            var dst = new Color32[w * ht];
            float sx = (float)res / w, sy = (float)res / ht;
            for (int y = 0; y < ht; y++)
            {
                float my = (y + 0.5f) * sy - 0.5f;
                int y0 = my < 0 ? 0 : (int)my; if (y0 > res - 1) y0 = res - 1;
                int y1 = y0 + 1 < res ? y0 + 1 : res - 1;
                float fy = Mathf.Clamp01(my - y0);
                for (int x = 0; x < w; x++)
                {
                    float mx = (x + 0.5f) * sx - 0.5f;
                    int x0 = mx < 0 ? 0 : (int)mx; if (x0 > res - 1) x0 = res - 1;
                    int x1 = x0 + 1 < res ? x0 + 1 : res - 1;
                    float fx = Mathf.Clamp01(mx - x0);
                    int a = y0 * res + x0, bb = y0 * res + x1, c = y1 * res + x0, d = y1 * res + x1;
                    float ln = line == null ? 0f : Mathf.Lerp(Mathf.Lerp(line[a], line[bb], fx), Mathf.Lerp(line[c], line[d], fx), fy);
                    float sh = shadow == null ? 0f : Mathf.Lerp(Mathf.Lerp(shadow[a], shadow[bb], fx), Mathf.Lerp(shadow[c], shadow[d], fx), fy);
                    int i = y * w + x;
                    var o = orig != null ? orig[i] : new Color32(0, 0, 0, 0);
                    int g = (int)(Mathf.Clamp01(sh) * 255f + 0.5f), bl = (int)(Mathf.Clamp01(ln) * 255f + 0.5f);
                    dst[i] = new Color32(o.r, (byte)Math.Max(o.g, g), (byte)Math.Max(o.b, bl), 255);
                }
            }
            return dst;
        }

        /// <summary>元の法線マップ (RGB / DXT5nm) を RGB 形式 (A=255) に変換。無ければ平ら。</summary>
        public static Color32[] NormalBasePixels(Color32[] orig, int w, int h)
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

        /// <summary>元の _DetailMask をそのまま (無ければ黒 = シェーダーの既定と同じ)。</summary>
        public static Color32[] DetailBasePixels(Color32[] orig, int w, int h) =>
            orig != null ? (Color32[])orig.Clone() : new Color32[w * h];

        public static Color32[] ReadLinear(Texture src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                var px = tmp.GetPixels32();
                UnityEngine.Object.Destroy(tmp);
                return px;
            }
            catch (Exception e)
            {
                WrinklePlugin.Log.LogDebug("法線マップを読めません: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }

    /// <summary>Improved Perlin noise (3D)。おおよそ -1..1。</summary>
    internal static class Perlin
    {
        private static readonly int[] P = BuildPerm();

        private static int[] BuildPerm()
        {
            var p = new int[512];
            var r = new System.Random(1234567);
            var src = new int[256];
            for (int i = 0; i < 256; i++) src[i] = i;
            for (int i = 255; i > 0; i--) { int j = r.Next(i + 1); int t = src[i]; src[i] = src[j]; src[j] = t; }
            for (int i = 0; i < 512; i++) p[i] = src[i & 255];
            return p;
        }

        public static float Noise(float x, float y, float z)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
            x -= xi; y -= yi; z -= zi;
            int X = xi & 255, Y = yi & 255, Z = zi & 255;
            float u = Fade(x), v = Fade(y), w = Fade(z);
            int A = P[X] + Y, AA = P[A] + Z, AB = P[A + 1] + Z;
            int B = P[X + 1] + Y, BA = P[B] + Z, BB = P[B + 1] + Z;
            return Lerp(w,
                Lerp(v, Lerp(u, Grad(P[AA], x, y, z), Grad(P[BA], x - 1, y, z)),
                        Lerp(u, Grad(P[AB], x, y - 1, z), Grad(P[BB], x - 1, y - 1, z))),
                Lerp(v, Lerp(u, Grad(P[AA + 1], x, y, z - 1), Grad(P[BA + 1], x - 1, y, z - 1)),
                        Lerp(u, Grad(P[AB + 1], x, y - 1, z - 1), Grad(P[BB + 1], x - 1, y - 1, z - 1))));
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static float Lerp(float t, float a, float b) => a + t * (b - a);

        private static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }
}
