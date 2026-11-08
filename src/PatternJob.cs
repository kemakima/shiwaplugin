using System;
using System.Threading;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>
    /// しわ模様の画像 (法線マップ・アニメ調の線/影) を作る重い計算を、別スレッドで行う (v0.5)。
    /// スライダーを動かしても Studio が固まらないようにするため。
    /// Unity の API は使わない (元の画像の読み出しとアップロードはメインスレッドで行う)。
    /// </summary>
    internal class PatternJob
    {
        // ---- 入力 (開始前にメインスレッドで用意し、以後は変更しない) ----
        public TexelBinding Binding;
        public float[] Tensor;
        public WrinkleSettings Settings;
        public string Key;
        public Color32[] NormalOrig; public int NW, NH; public bool NormalBase;
        public bool Toon;
        public Color32[] DetailOrig; public int DW, DH; public bool DetailBase;

        // ---- 出力 ----
        public Color32[] NormalBasePx, NormalFull1, NormalFull2;
        public Color32[] DetailBasePx, DetailFull1, DetailFull2;
        public long Ms;
        public Exception Error;
        private volatile bool _done;
        public bool Done => _done;

        public void Start()
        {
            if (!ThreadPool.QueueUserWorkItem(_ => Run())) Run();
        }

        public void Run()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var s = Settings;
                var L = WrinkleBaker.BuildLayers(Binding, Tensor, s, Toon);
                if (NormalBase) NormalBasePx = WrinkleBaker.NormalBasePixels(NormalOrig, NW, NH);
                NormalFull1 = WrinkleBaker.Compose(L.H1, L.Res, NormalOrig, NW, NH, s.Strength);
                if (L.H2 != null) NormalFull2 = WrinkleBaker.Compose(L.H2, L.Res, NormalOrig, NW, NH, s.Strength);
                if (Toon)
                {
                    if (DetailBase) DetailBasePx = WrinkleBaker.DetailBasePixels(DetailOrig, DW, DH);
                    DetailFull1 = WrinkleBaker.ComposeDetail(L.L1, L.S1, L.Res, DetailOrig, DW, DH);
                    if (L.L2 != null) DetailFull2 = WrinkleBaker.ComposeDetail(L.L2, L.S2, L.Res, DetailOrig, DW, DH);
                }
            }
            catch (Exception e) { Error = e; }
            finally
            {
                Ms = sw.ElapsedMilliseconds;
                _done = true;
            }
        }
    }
}
