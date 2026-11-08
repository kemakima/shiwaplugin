using System;
using System.Collections.Generic;
using ExtensibleSaveFormat;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>キャラ1人分のしわ設定。</summary>
    public class WrinkleSettings
    {
        public bool Enabled;
        public float Strength = 0.5f;     // しわの強さ (法線の傾き) 0..1
        public float Detail = 5f;         // しわの細かさ 1..20 (v0.2.1 で既定 12→5)
        public float Sensitivity = 8f;    // 縮みへの反応 1..30
        public float Threshold = 0.03f;   // これ以下の縮みは無視 0..0.3
        public float Tension = 0.3f;      // 張りじわ (引っ張られた所) の割合 0..1
        public float Sharpness = 0.5f;    // 折り目の鋭さ 0..1
        public int Seed;                  // しわ模様 0..100
        public bool Realtime = true;      // ポーズに合わせて毎フレーム更新 (v0.3)
        public float Align = 1f;          // しわの向きを横じわ (体・手足を一周する向き) に揃える 0..1 (v0.3 から未使用)
        public float DeepFolds = 0.6f;    // 深く曲げたときに増える折り目 (2層目) 0..1 (v0.5)
        public float ToonLine = 0.6f;     // アニメ調のしわ線 (_DetailMask の B = 線) 0..1 (v0.5)
        public float ToonShadow = 0.6f;   // アニメ調のしわ影 (_DetailMask の G = 描き込みの影) 0..1 (v0.5)
        public int SlotMask = DefaultSlots;

        // 0 トップス, 1 ボトムス, 2 インナー上, 3 インナー下, 4 手袋, 5 パンスト, 6 靴下, 7/8 靴
        public const int DefaultSlots = (1 << 7) - 1;
        public static readonly string[] SlotNames = { "トップス", "ボトムス", "インナー上", "インナー下", "手袋", "パンスト", "靴下", "靴" };

        public bool HasSlot(int i)
        {
            if (i >= 8) i = 7; // 靴 (内/外) はまとめて1つ
            return (SlotMask & (1 << i)) != 0;
        }

        public void SetSlot(int i, bool on) => SlotMask = on ? SlotMask | (1 << i) : SlotMask & ~(1 << i);

        public WrinkleSettings Clone() => (WrinkleSettings)MemberwiseClone();

        /// <summary>しわの見た目に関わる値 (変化検出用)。</summary>
        public string LookKey() =>
            $"{Strength:F3}|{Detail:F3}|{Sensitivity:F3}|{Threshold:F3}|{Tension:F3}|{Sharpness:F3}|{Seed}|{Align:F3}|{DeepFolds:F3}|{ToonLine:F3}|{ToonShadow:F3}";

        /// <summary>しわ模様 (最初に1回だけ作るもの) に関わる値。</summary>
        public string PatternKey() => $"{Strength:F3}|{Detail:F3}|{Sharpness:F3}|{Seed}|{DeepFolds:F3}|{ToonLine:F3}|{ToonShadow:F3}";

        /// <summary>法線マップのしわ模様だけに関わる値 (アニメ調を使わないマテリアル用)。</summary>
        public string NormalKey() => $"{Strength:F3}|{Detail:F3}|{Sharpness:F3}|{Seed}|{DeepFolds:F3}";

        /// <summary>アニメ調 (線・影) を描くか。</summary>
        public bool UseToon => ToonLine > 0.001f || ToonShadow > 0.001f;

        public bool IsDefault()
        {
            var d = new WrinkleSettings();
            return Enabled == d.Enabled && LookKey() == d.LookKey() && Realtime == d.Realtime && SlotMask == d.SlotMask;
        }

        public PluginData Save()
        {
            var pd = new PluginData { version = 2 };
            pd.data["Enabled"] = Enabled;
            pd.data["Strength"] = Strength;
            pd.data["Detail"] = Detail;
            pd.data["Sensitivity"] = Sensitivity;
            pd.data["Threshold"] = Threshold;
            pd.data["Tension"] = Tension;
            pd.data["Sharpness"] = Sharpness;
            pd.data["Seed"] = Seed;
            pd.data["Align"] = Align;
            pd.data["DeepFolds"] = DeepFolds;
            pd.data["ToonLine"] = ToonLine;
            pd.data["ToonShadow"] = ToonShadow;
            pd.data["Realtime"] = Realtime;
            pd.data["SlotMask"] = SlotMask;
            return pd;
        }

        public static WrinkleSettings Load(PluginData pd)
        {
            var s = new WrinkleSettings();
            if (pd?.data == null) return s;
            var d = pd.data;
            s.Enabled = GetBool(d, "Enabled", s.Enabled);
            s.Strength = GetFloat(d, "Strength", s.Strength);
            s.Detail = GetFloat(d, "Detail", s.Detail);
            s.Sensitivity = GetFloat(d, "Sensitivity", s.Sensitivity);
            s.Threshold = GetFloat(d, "Threshold", s.Threshold);
            s.Tension = GetFloat(d, "Tension", s.Tension);
            s.Sharpness = GetFloat(d, "Sharpness", s.Sharpness);
            s.Seed = GetInt(d, "Seed", s.Seed);
            s.Align = GetFloat(d, "Align", s.Align);
            s.DeepFolds = GetFloat(d, "DeepFolds", s.DeepFolds);
            s.ToonLine = GetFloat(d, "ToonLine", s.ToonLine);
            s.ToonShadow = GetFloat(d, "ToonShadow", s.ToonShadow);
            s.Realtime = GetBool(d, "Realtime", s.Realtime);
            s.SlotMask = GetInt(d, "SlotMask", s.SlotMask);
            return s;
        }

        private static float GetFloat(Dictionary<string, object> d, string k, float def)
        {
            try { return d.TryGetValue(k, out var v) && v != null ? Convert.ToSingle(v) : def; }
            catch { return def; }
        }

        private static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            try { return d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : def; }
            catch { return def; }
        }

        private static bool GetBool(Dictionary<string, object> d, string k, bool def)
        {
            try { return d.TryGetValue(k, out var v) && v != null ? Convert.ToBoolean(v) : def; }
            catch { return def; }
        }
    }
}
