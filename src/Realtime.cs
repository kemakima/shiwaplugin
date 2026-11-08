using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>
    /// リアルタイム版の処理。
    /// ・しわ模様 (横じわ、最大の濃さ) は最初に1回だけ作る
    /// ・毎フレームは、頂点ごとの「しわの濃さ」だけを計算して、UV展開したメッシュの頂点色に入れる
    /// ・GPU で「元の法線」の上に「しわ最大の法線」を頂点色アルファで重ねて描く
    /// </summary>
    internal static class Realtime
    {
        // ---------------- 準備 ----------------

        /// <summary>しわの向き = 体・手足の長さ方向 (面に投影)。量はすべて 1。</summary>
        public static float[] PatternTensor(List<MeshPart> parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.WeldCount;
            var M = new float[total * 6];
            var W = new float[total];
            foreach (var p in parts)
            {
                var rest = p.Rest;
                for (int t = 0; t + 2 < p.Tris.Length; t += 3)
                {
                    int ia = p.Tris[t], ib = p.Tris[t + 1], ic = p.Tris[t + 2];
                    var nrm = Vector3.Cross(rest[ib] - rest[ia], rest[ic] - rest[ia]);
                    float area = nrm.magnitude;
                    if (area < 1e-12f) continue;
                    nrm /= area;
                    var ax = p.Axis != null ? p.Axis[ia] + p.Axis[ib] + p.Axis[ic] : Vector3.up;
                    ax -= nrm * Vector3.Dot(ax, nrm);
                    float al = ax.magnitude;
                    if (al < 1e-4f) continue;
                    ax /= al;
                    WrinkleBaker.Add(M, W, p.WeldOffset + p.Weld[ia], ax, area, area);
                    WrinkleBaker.Add(M, W, p.WeldOffset + p.Weld[ib], ax, area, area);
                    WrinkleBaker.Add(M, W, p.WeldOffset + p.Weld[ic], ax, area, area);
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

        /// <summary>三角形ごとの元の形 (2D) と、UV展開メッシュを作る。</summary>
        public static void PreparePart(MeshPart p)
        {
            int nt = p.Tris.Length / 3;
            p.TriL1 = new float[nt]; p.TriBx = new float[nt]; p.TriBy = new float[nt]; p.TriArea = new float[nt];
            p.TriAx = new float[nt]; p.TriAy = new float[nt];
            var rest = p.Rest;
            for (int t = 0; t < nt; t++)
            {
                int ia = p.Tris[t * 3], ib = p.Tris[t * 3 + 1], ic = p.Tris[t * 3 + 2];
                Vector3 e1 = rest[ib] - rest[ia], e2 = rest[ic] - rest[ia];
                var nrm = Vector3.Cross(e1, e2);
                float area = nrm.magnitude, l1 = e1.magnitude;
                if (area < 1e-12f || l1 < 1e-7f) continue;
                var T = e1 / l1;
                var B = Vector3.Cross(nrm / area, T);
                float by = Vector3.Dot(e2, B);
                if (Mathf.Abs(by) < 1e-9f) continue;
                p.TriL1[t] = l1; p.TriBx[t] = Vector3.Dot(e2, T); p.TriBy[t] = by; p.TriArea[t] = area;
                // 体の軸を面に投影して2Dに (横じわ = この向きの縮みで出る)
                var ax = p.Axis != null ? p.Axis[ia] + p.Axis[ib] + p.Axis[ic] : Vector3.up;
                float x = Vector3.Dot(ax, T), y = Vector3.Dot(ax, B), len = Mathf.Sqrt(x * x + y * y);
                if (len > 0.3f) { p.TriAx[t] = x / len; p.TriAy[t] = y / len; } // 軸が面とほぼ直角なら 0 (主ひずみで代用)
            }
            SetReference(p, null);

            p.Colors = new Color32[p.Rest.Length];
            for (int i = 0; i < p.Colors.Length; i++) p.Colors[i] = new Color32(255, 255, 255, 0);
        }

        /// <summary>
        /// ボーンの回転だけをバインド姿勢 (直立) に戻して BakeMesh し、すぐ元に戻す。
        /// 位置・拡大率 (= キャラメイクの体型) はそのままなので、「体型込みの直立姿勢の形」になる。
        /// </summary>
        public static Vector3[] BakeInBindPose(SkinnedMeshRenderer r, ref Mesh baked)
        {
            var mesh = r.sharedMesh;
            var bones = r.bones;
            var bind = mesh.bindposes;
            if (bones == null || bind == null || bones.Length != bind.Length || bones.Length == 0) return null;

            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && !index.ContainsKey(bones[i])) index[bones[i]] = i;
            var qBind = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) qBind[i] = bind[i].inverse.rotation;

            // 親から順に処理する
            var order = new List<int>(index.Values);
            var depth = new int[bones.Length];
            foreach (var i in order) { int d = 0; for (var t = bones[i].parent; t != null; t = t.parent) d++; depth[i] = d; }
            order.Sort((a, b) => depth[a].CompareTo(depth[b]));

            var saved = new Quaternion[bones.Length];
            foreach (var i in order) saved[i] = bones[i].localRotation;
            try
            {
                foreach (var i in order)
                {
                    // いちばん近い、リストにある祖先からの相対回転をバインド時と同じにする
                    int ai = -1;
                    for (var t = bones[i].parent; t != null; t = t.parent)
                        if (index.TryGetValue(t, out ai)) break; else ai = -1;
                    if (ai < 0) continue;
                    bones[i].rotation = bones[ai].rotation * (Quaternion.Inverse(qBind[ai]) * qBind[i]);
                }
                if (baked == null) baked = new Mesh();
                r.BakeMesh(baked);
                var v = baked.vertices;
                return v.Length == mesh.vertexCount ? v : null;
            }
            finally
            {
                foreach (var i in order) bones[i].localRotation = saved[i];
            }
        }

        /// <summary>基準の形を設定 (null = 元の形)。</summary>
        public static void SetReference(MeshPart p, Vector3[] rf)
        {
            p.Reference = rf;
            int nt = p.Tris.Length / 3;
            var src = rf ?? p.Rest;
            double sum = 0;
            for (int t = 0; t < nt; t++)
            {
                int ia = p.Tris[t * 3], ib = p.Tris[t * 3 + 1], ic = p.Tris[t * 3 + 2];
                sum += Vector3.Cross(src[ib] - src[ia], src[ic] - src[ia]).magnitude;
            }
            p.RefAreaSum = sum;
            if (rf == null) { p.RefB = null; return; }
            p.RefB = new float[nt * 3];
            for (int t = 0; t < nt; t++)
            {
                if (p.TriL1[t] <= 0f) continue;
                int ia = p.Tris[t * 3], ib = p.Tris[t * 3 + 1], ic = p.Tris[t * 3 + 2];
                WrinkleBaker.Metric(rf[ib] - rf[ia], rf[ic] - rf[ia], p.TriL1[t], p.TriBx[t], p.TriBy[t],
                    out p.RefB[t * 3], out p.RefB[t * 3 + 1], out p.RefB[t * 3 + 2]);
            }
        }

        /// <summary>
        /// キャラの全パーツ (インナー上下など別メッシュ・別マテリアルも含む) をまたいで、
        /// 同じ位置 (許容差 tol 以内) の頂点に共通の番号を付ける。境目で濃さが途切れないようにするため。
        /// </summary>
        public static int GlobalWeld(List<MeshPart> parts, float tol)
        {
            float inv = 1f / Mathf.Max(1e-6f, tol);
            var grid = new Dictionary<long, List<int>>();
            var reps = new List<Vector3>();
            foreach (var p in parts)
            {
                p.GWeld = new int[p.Rest.Length];
                for (int i = 0; i < p.Rest.Length; i++)
                {
                    var v = p.Rest[i];
                    int cx = Mathf.FloorToInt(v.x * inv), cy = Mathf.FloorToInt(v.y * inv), cz = Mathf.FloorToInt(v.z * inv);
                    int found = -1;
                    for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dz = -1; dz <= 1 && found < 0; dz++)
                    {
                        if (!grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var list)) continue;
                        foreach (var id in list)
                            if ((reps[id] - v).sqrMagnitude <= tol * tol) { found = id; break; }
                    }
                    if (found < 0)
                    {
                        found = reps.Count;
                        reps.Add(v);
                        long k = Key(cx, cy, cz);
                        if (!grid.TryGetValue(k, out var l2)) { l2 = new List<int>(2); grid[k] = l2; }
                        l2.Add(found);
                    }
                    p.GWeld[i] = found;
                }
            }
            foreach (var p in parts) p.GWeldTotal = reps.Count;
            return reps.Count;
        }

        private static long Key(int x, int y, int z) =>
            ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        // ---------------- 毎フレーム ----------------

        private static float[] _sumA = new float[0], _sumW = new float[0];

        /// <summary>今のポーズから頂点ごとのしわの濃さを求め、UVメッシュの頂点色に入れる。</summary>
        public static void UpdateAmounts(List<MeshPart> parts, WrinkleSettings s, bool bake = true)
        {
            int total = 0;
            foreach (var p in parts) if (p.GWeld != null) total = Math.Max(total, p.GWeldTotal);
            if (_sumA.Length < total) { _sumA = new float[total]; _sumW = new float[total]; }
            Array.Clear(_sumA, 0, total);
            Array.Clear(_sumW, 0, total);

            // 現在の形
            double areaCur = 0, areaRef = 0;
            foreach (var p in parts)
            {
                if (bake)
                {
                    if (p.Baked == null) p.Baked = new Mesh();
                    p.Renderer.BakeMesh(p.Baked);
                    p.Baked.GetVertices(p.CurBuf);
                }
                if (p.CurBuf.Count != p.Rest.Length) continue;
                var v = p.CurBuf;
                for (int t = 0; t + 2 < p.Tris.Length; t += 3)
                {
                    int a = p.Tris[t], b = p.Tris[t + 1], c = p.Tris[t + 2];
                    areaCur += Vector3.Cross(v[b] - v[a], v[c] - v[a]).magnitude;
                }
                areaRef += p.RefAreaSum;
            }
            float g2 = areaRef > 1e-12 ? (float)(areaCur / areaRef) : 1f;
            if (g2 < 1e-6f) g2 = 1f;
            float invG2 = 1f / g2;
            float th = s.Threshold;

            foreach (var p in parts)
            {
                if (p.CurBuf.Count != p.Rest.Length) continue;
                var v = p.CurBuf;
                int nt = p.Tris.Length / 3;
                for (int t = 0; t < nt; t++)
                {
                    float l1 = p.TriL1[t];
                    if (l1 <= 0f) continue;
                    int ia = p.Tris[t * 3], ib = p.Tris[t * 3 + 1], ic = p.Tris[t * 3 + 2];
                    WrinkleBaker.Metric(v[ib] - v[ia], v[ic] - v[ia], l1, p.TriBx[t], p.TriBy[t], out var a11, out var a12, out var a22);
                    a11 *= invG2; a12 *= invG2; a22 *= invG2;
                    float b11 = 1f, b12 = 0f, b22 = 1f;
                    if (p.RefB != null) { b11 = p.RefB[t * 3]; b12 = p.RefB[t * 3 + 1]; b22 = p.RefB[t * 3 + 2]; }

                    float comp;
                    float ux = p.TriAx[t], uy = p.TriAy[t];
                    if (ux != 0f || uy != 0f)
                    {
                        // 体の軸方向の伸び縮みだけを見る (横から押されたり、伸びたりしても横じわは出ない)
                        float num = ux * ux * a11 + 2f * ux * uy * a12 + uy * uy * a22;
                        float den = ux * ux * b11 + 2f * ux * uy * b12 + uy * uy * b22;
                        if (den < 1e-12f) continue;
                        comp = Mathf.Max(0f, 1f - Mathf.Sqrt(Mathf.Max(0f, num / den)) - th);
                    }
                    else
                    {
                        float qa = b11 * b22 - b12 * b12;
                        if (qa < 1e-12f) continue;
                        float qb = a11 * b22 + a22 * b11 - 2f * a12 * b12;
                        float qc = a11 * a22 - a12 * a12;
                        float disc = qb * qb - 4f * qa * qc;
                        if (disc < 0f) disc = 0f;
                        float lMin = (qb - Mathf.Sqrt(disc)) / (2f * qa);
                        comp = Mathf.Max(0f, 1f - Mathf.Sqrt(Mathf.Max(lMin, 0f)) - th);
                    }
                    float amount = comp;

                    float wt = p.TriArea[t];
                    int g0 = p.GWeld[ia], g1 = p.GWeld[ib], g2i = p.GWeld[ic];
                    _sumA[g0] += amount * wt; _sumW[g0] += wt;
                    _sumA[g1] += amount * wt; _sumW[g1] += wt;
                    _sumA[g2i] += amount * wt; _sumW[g2i] += wt;
                }
            }

            float sens = s.Sensitivity;
            foreach (var p in parts)
            {
                var col = p.Colors;
                for (int i = 0; i < col.Length; i++)
                {
                    int g = p.GWeld[i];
                    float a = _sumW[g] > 0f ? _sumA[g] / _sumW[g] : 0f;
                    float alpha = 1f - Mathf.Exp(-a * sens);
                    col[i].a = (byte)(alpha * 255f);
                }
            }
        }

        // ---------------- GPU 合成 ----------------

        private static Material _mat;

        /// <summary>
        /// out = base の上に、full を頂点の濃さ (アルファ) で重ねる (RGB のみ)。
        /// GL の即時描画で三角形を UV の位置に描く (DirtFX で動作実績のある方法)。濃さ 0 の三角形は描かない。
        /// </summary>
        public static int Draw(RenderTexture dst, Texture baseTex, Texture fullTex, List<MeshPart> parts)
        {
            if (_mat == null)
            {
                var shader = Shader.Find("UI/Default");
                if (shader == null) { WrinklePlugin.Log.LogError("UI/Default シェーダーが見つかりません"); return 0; }
                _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetFloat("_ColorMask", 14f); // RGB のみ (A は 1 のまま)
                _mat.color = Color.white;
            }
            Graphics.Blit(baseTex, dst);

            int drawn = 0;
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            GL.PushMatrix();
            GL.LoadOrtho();
            _mat.mainTexture = fullTex;
            _mat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            foreach (var p in parts)
            {
                var uv = p.Uv; var col = p.Colors; var tris = p.Tris;
                if (uv == null || col == null || uv.Length != col.Length) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    if (col[ia].a == 0 && col[ib].a == 0 && col[ic].a == 0) continue;
                    Vector2 a = uv[ia], b = uv[ib], c = uv[ic];
                    var center = (a + b + c) / 3f;
                    var off = new Vector2(Mathf.Floor(center.x), Mathf.Floor(center.y));
                    a -= off; b -= off; c -= off;
                    GL.Color(new Color(1f, 1f, 1f, col[ia].a / 255f)); GL.TexCoord2(a.x, a.y); GL.Vertex3(a.x, a.y, 0f);
                    GL.Color(new Color(1f, 1f, 1f, col[ib].a / 255f)); GL.TexCoord2(b.x, b.y); GL.Vertex3(b.x, b.y, 0f);
                    GL.Color(new Color(1f, 1f, 1f, col[ic].a / 255f)); GL.TexCoord2(c.x, c.y); GL.Vertex3(c.x, c.y, 0f);
                    drawn++;
                }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
            return drawn;
        }

        /// <summary>RenderTexture / Texture を PNG に保存 (調査用)。</summary>
        public static void Dump(Texture tex, string path)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                t2.Apply();
                System.IO.File.WriteAllBytes(path, t2.EncodeToPNG());
                UnityEngine.Object.Destroy(t2);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
