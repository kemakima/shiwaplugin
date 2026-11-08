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
        public Mesh UvMesh;                          // UV展開した形のメッシュ (頂点色アルファ = しわの濃さ)
        public Color32[] Colors;
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

            // 継ぎ目のにじみ防止: 有効テクセルを周りに3回広げる (隣の割り当てをコピー)
            for (int pass = 0; pass < 3; pass++)
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

        // ---------------- 縮み・伸び (ポーズが変わるたび) ----------------

        public struct StrainStats { public float MaxCompression, MaxStretch, MaxAmount; }

        /// <summary>
        /// 頂点(溶接ID)ごとに「しわテンソル」M = Σ 量 × d dᵀ を作る (d = 縮む向き, 元の形の空間)。
        /// 6要素 (xx, yy, zz, xy, xz, yz) × 溶接数 の配列を返す。
        /// </summary>
        public static float[] ComputeTensor(List<MeshPart> parts, WrinkleSettings s, out StrainStats stats)
        {
            // 現在の形を取得
            var cur = new Vector3[parts.Count][];
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var p = parts[pi];
                if (p.Baked == null) p.Baked = new Mesh();
                p.Renderer.BakeMesh(p.Baked);
                var v = p.Baked.vertices;
                cur[pi] = v.Length == p.Rest.Length ? v : null;
            }
            return ComputeTensor(parts, cur, s, out stats);
        }

        public static float[] ComputeTensor(List<MeshPart> parts, Vector3[][] cur, WrinkleSettings s, out StrainStats stats)
        {
            stats = new StrainStats();
            int total = 0;
            foreach (var p in parts) total += p.WeldCount;
            var M = new float[total * 6];
            var W = new float[total];
            double areaCur = 0, areaRef = 0;
            for (int pi = 0; pi < parts.Count; pi++)
            {
                var p = parts[pi];
                var v = cur[pi];
                if (v == null) continue;
                var rf = p.Reference ?? p.Rest;
                for (int t = 0; t + 2 < p.Tris.Length; t += 3)
                {
                    int a = p.Tris[t], b = p.Tris[t + 1], c = p.Tris[t + 2];
                    areaCur += Vector3.Cross(v[b] - v[a], v[c] - v[a]).magnitude;
                    areaRef += Vector3.Cross(rf[b] - rf[a], rf[c] - rf[a]).magnitude;
                }
            }
            // 全体の大きさの違い (レンダラーの拡大率や体型) は打ち消す
            float g2 = areaRef > 1e-12 ? (float)(areaCur / areaRef) : 1f;
            if (g2 < 1e-6f) g2 = 1f;
            float invG2 = 1f / g2;

            float th = s.Threshold;
            float tension = s.Tension;
            float align = Mathf.Clamp01(s.Align);

            for (int pi = 0; pi < parts.Count; pi++)
            {
                var p = parts[pi];
                var v = cur[pi];
                if (v == null) continue;
                var rest = p.Rest;
                var rf = p.Reference;

                for (int t = 0; t + 2 < p.Tris.Length; t += 3)
                {
                    int ia = p.Tris[t], ib = p.Tris[t + 1], ic = p.Tris[t + 2];
                    Vector3 e1 = rest[ib] - rest[ia], e2 = rest[ic] - rest[ia];
                    var nrm = Vector3.Cross(e1, e2);
                    float area = nrm.magnitude;
                    float l1 = e1.magnitude;
                    if (area < 1e-12f || l1 < 1e-7f) continue;
                    var T = e1 / l1;
                    var B = Vector3.Cross(nrm / area, T);
                    // 元の形の三角形を2Dに: p1 = (l1, 0), p2 = (bx, by)
                    float bx = Vector3.Dot(e2, T), by = Vector3.Dot(e2, B);
                    if (Mathf.Abs(by) < 1e-9f) continue;

                    // 現在の形の計量テンソル (2x2)
                    Metric(v[ib] - v[ia], v[ic] - v[ia], l1, bx, by, out var a11, out var a12, out var a22);
                    a11 *= invG2; a12 *= invG2; a22 *= invG2;

                    float b11 = 1f, b12 = 0f, b22 = 1f;
                    if (rf != null) Metric(rf[ib] - rf[ia], rf[ic] - rf[ia], l1, bx, by, out b11, out b12, out b22);

                    // 一般化固有値 A v = λ B v
                    float qa = b11 * b22 - b12 * b12;
                    float qb = a11 * b22 + a22 * b11 - 2f * a12 * b12;
                    float qc = a11 * a22 - a12 * a12;
                    if (qa < 1e-12f) continue;
                    float disc = qb * qb - 4f * qa * qc;
                    if (disc < 0f) disc = 0f;
                    float sq = Mathf.Sqrt(disc);
                    float lMax = (qb + sq) / (2f * qa);
                    float lMin = (qb - sq) / (2f * qa);
                    if (lMin < 1e-6f) lMin = 1e-6f;
                    float sMax = Mathf.Sqrt(lMax), sMin = Mathf.Sqrt(lMin);

                    // 縮む向き (λMin の固有ベクトル)
                    float r0 = a11 - lMin * b11, r1 = a12 - lMin * b12;
                    float vx = r1, vy = -r0;
                    if (vx * vx + vy * vy < 1e-10f)
                    {
                        float r2 = a12 - lMin * b12, r3 = a22 - lMin * b22;
                        vx = r3; vy = -r2;
                        if (vx * vx + vy * vy < 1e-10f) { vx = 1f; vy = 0f; }
                    }
                    var d = (T * vx + B * vy).normalized;

                    // 横じわに揃える: 縮む向きを長さ方向 (面に投影) へ寄せる → 折り目は体を一周する向き
                    if (align > 0f && p.Axis != null)
                    {
                        var ax = p.Axis[ia] + p.Axis[ib] + p.Axis[ic];
                        var nn = nrm / area;
                        ax -= nn * Vector3.Dot(ax, nn);
                        float al = ax.magnitude;
                        if (al > 1e-4f)
                        {
                            ax /= al;
                            if (Vector3.Dot(ax, d) < 0f) ax = -ax;
                            d = Vector3.Lerp(d, ax, align).normalized;
                            if (d.sqrMagnitude < 0.5f) d = ax;
                        }
                    }

                    float comp = Mathf.Max(0f, 1f - sMin - th);
                    float aniso = Mathf.Max(0f, sMax / sMin - 1f - th);
                    float amount = comp + tension * aniso * 0.25f;

                    if (1f - sMin > stats.MaxCompression) stats.MaxCompression = 1f - sMin;
                    if (sMax - 1f > stats.MaxStretch) stats.MaxStretch = sMax - 1f;
                    if (amount > stats.MaxAmount) stats.MaxAmount = amount;

                    float wt = area;
                    float m = amount * wt;
                    Add(M, W, p.WeldOffset + p.Weld[ia], d, m, wt);
                    Add(M, W, p.WeldOffset + p.Weld[ib], d, m, wt);
                    Add(M, W, p.WeldOffset + p.Weld[ic], d, m, wt);
                }
            }

            for (int i = 0; i < total; i++)
            {
                if (W[i] <= 0f) continue;
                float inv = 1f / W[i];
                for (int k = 0; k < 6; k++) M[i * 6 + k] *= inv;
            }
            return M;
        }

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

        // ---------------- しわの高さ (テクセル) ----------------

        /// <summary>しわの高さマップ (Res×Res)。0 = 平ら、負 = 折り目のくぼみ。</summary>
        public static float[] BuildHeight(TexelBinding b, float[] M, WrinkleSettings s, bool full = false)
        {
            int res = b.Res;
            var h = new float[res * res];
            float f = s.Detail;
            const float aniso = 4f; // 縮む向きの折り目の間隔 = 1 / (細かさ × 4)
            float sharpExp = 1f + s.Sharpness * 7f;
            float sens = s.Sensitivity;
            var seed = new Vector3(s.Seed * 13.37f % 101f, s.Seed * 7.13f % 97f, s.Seed * 3.71f % 89f) + new Vector3(5.3f, 11.9f, 2.7f);

            for (int k = 0; k < b.Count; k++)
            {
                // テンソルを補間
                float w0 = b.W0[k], w1 = b.W1[k], w2 = b.W2[k];
                int o0 = b.V0[k] * 6, o1 = b.V1[k] * 6, o2 = b.V2[k] * 6;
                float xx = M[o0] * w0 + M[o1] * w1 + M[o2] * w2;
                float yy = M[o0 + 1] * w0 + M[o1 + 1] * w1 + M[o2 + 1] * w2;
                float zz = M[o0 + 2] * w0 + M[o1 + 2] * w1 + M[o2 + 2] * w2;
                if (xx + yy + zz < 1e-5f) continue;
                float xy = M[o0 + 3] * w0 + M[o1 + 3] * w1 + M[o2 + 3] * w2;
                float xz = M[o0 + 4] * w0 + M[o1 + 4] * w1 + M[o2 + 4] * w2;
                float yz = M[o0 + 5] * w0 + M[o1 + 5] * w1 + M[o2 + 5] * w2;

                // いちばん大きい固有ベクトル (べき乗法)
                Vector3 d;
                if (xx >= yy && xx >= zz) d = new Vector3(xx, xy, xz);
                else if (yy >= zz) d = new Vector3(xy, yy, yz);
                else d = new Vector3(xz, yz, zz);
                for (int it = 0; it < 3; it++)
                {
                    d = new Vector3(xx * d.x + xy * d.y + xz * d.z, xy * d.x + yy * d.y + yz * d.z, xz * d.x + yz * d.y + zz * d.z);
                    float len = d.magnitude;
                    if (len < 1e-12f) break;
                    d /= len;
                }
                float lam = xx * d.x * d.x + yy * d.y * d.y + zz * d.z * d.z + 2f * (xy * d.x * d.y + xz * d.x * d.z + yz * d.y * d.z);
                if (lam < 1e-5f) continue;
                float amp = full ? 1f : 1f - Mathf.Exp(-lam * sens);

                // 縮む向きに細かく、直角方向に粗いノイズ → 縮みと直角に走る折り目。
                var p = b.Pos[k];
                float fold;
                if (full)
                {
                    // リアルタイム版: 向きは体の軸で決まっているので、その向き1本だけのしま模様にする
                    // (複数の向きを混ぜると、しまが交差して×印に見えるため)
                    float up = d.y;
                    if (up < 0f) { d = -d; up = -up; }           // 向きの符号をそろえる (位相が反転しないように)
                    if (up > 0.9f) d = Vector3.up;               // 胴体・脚はまっすぐ上下に固定
                    else if (Mathf.Abs(up) < 1e-3f && d.x < 0f) d = -d;
                    fold = StripeCoord(p, b.Coord[k], f, aniso, sharpExp, seed);
                }
                else
                {
                    // 向きが場所で変わっても模様が歪まないよう、固定した方向ごとのしま模様を d に近い順に3つ混ぜる
                    int i1 = -1, i2 = -1, i3 = -1; float s1 = 0f, s2 = 0f, s3 = 0f;
                    for (int di = 0; di < Dirs.Length; di++)
                    {
                        float c = Vector3.Dot(Dirs[di], d); c = c * c; c = c * c * c; c = c * c; // |cos|^12
                        if (c > s1) { i3 = i2; s3 = s2; i2 = i1; s2 = s1; i1 = di; s1 = c; }
                        else if (c > s2) { i3 = i2; s3 = s2; i2 = di; s2 = c; }
                        else if (c > s3) { i3 = di; s3 = c; }
                    }
                    float wsum = 0f; fold = 0f;
                    if (i1 >= 0) { fold += s1 * Stripe(p, Dirs[i1], i1, f, aniso, sharpExp, seed); wsum += s1; }
                    if (i2 >= 0) { fold += s2 * Stripe(p, Dirs[i2], i2, f, aniso, sharpExp, seed); wsum += s2; }
                    if (i3 >= 0) { fold += s3 * Stripe(p, Dirs[i3], i3, f, aniso, sharpExp, seed); wsum += s3; }
                    if (wsum <= 0f) continue;
                    fold /= wsum;
                }
                // 折り目の長さに変化を付ける
                float breakup = 0.55f + 0.45f * (Perlin.Noise(p.x * f * 0.7f + 41f, p.y * f * 0.7f + 7f, p.z * f * 0.7f + 23f) * 0.5f + 0.5f);
                h[b.Texel[k]] = -amp * fold * breakup;
            }
            return h;
        }

        /// <summary>半球上にほぼ均等に並べた方向 (フィボナッチ)。</summary>
        private static readonly Vector3[] Dirs = BuildDirs(16);

        private static Vector3[] BuildDirs(int n)
        {
            var r = new Vector3[n];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float z = 1f - (i + 0.5f) / n;          // 0..1 (半球)
                float rad = Mathf.Sqrt(1f - z * z);
                float th = golden * i;
                r[i] = new Vector3(Mathf.Cos(th) * rad, z, Mathf.Sin(th) * rad); // y を極に
            }
            return r;
        }

        /// <summary>軸に沿った座標 coord で位相を決めるしま模様 (関節で向きが変わっても間隔が詰まらない)。</summary>
        private static float StripeCoord(Vector3 p, float coord, float f, float aniso, float sharpExp, Vector3 seed)
        {
            var q = p * f + seed;
            float warp = Perlin.Noise(q.x, q.y, q.z) * 1.1f + Perlin.Noise(q.x * 2.1f + 5.2f, q.y * 2.1f + 1.7f, q.z * 2.1f + 8.4f) * 0.35f;
            float phase = (coord * f * aniso + warp) * (2f * Mathf.PI);
            return Mathf.Pow(0.5f + 0.5f * Mathf.Cos(phase), sharpExp);
        }

        /// <summary>
        /// 方向 u に並ぶ、波打った平行な折り目 (0..1, 1 = 折り目の中心)。
        /// 位相を低周波ノイズでずらして、まっすぐすぎない線にする。
        /// </summary>
        private static float Stripe(Vector3 p, Vector3 u, int idx, float f, float aniso, float sharpExp, Vector3 seed)
        {
            var o = seed + new Vector3(idx * 31.7f, idx * 17.3f, idx * 11.1f);
            var q = p * f + o;
            float warp = Perlin.Noise(q.x, q.y, q.z) * 1.1f + Perlin.Noise(q.x * 2.1f + 5.2f, q.y * 2.1f + 1.7f, q.z * 2.1f + 8.4f) * 0.35f;
            float phase = (Vector3.Dot(u, p) * f * aniso + warp) * (2f * Mathf.PI);
            return Mathf.Pow(0.5f + 0.5f * Mathf.Cos(phase), sharpExp);
        }

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
