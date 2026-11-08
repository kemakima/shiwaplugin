using System;
using KKAPI.Studio;
using KKAPI.Studio.UI;
using Studio;
using UniRx;
using UnityEngine;

namespace ClothWrinkle
{
    /// <summary>Studio の「アニメ → 現在の状態」に「服のしわ」カテゴリを追加する。選択中のキャラ全員に適用。</summary>
    internal static class StudioUI
    {
        public static void Register()
        {
            var cat = StudioAPI.GetOrCreateCurrentStateCategory("服のしわ");

            cat.AddControl(new CurrentStateCategorySwitch("しわを付ける", c => Get(c)?.Settings.Enabled ?? false))
                .Value.Subscribe(v => Each(s => s.Enabled = v));

            cat.AddControl(new CurrentStateCategorySlider("強さ", c => Get(c)?.Settings.Strength ?? 0.5f, 0f, 1f))
                .Value.Subscribe(v => Each(s => s.Strength = v));

            cat.AddControl(new CurrentStateCategorySlider("しわの細かさ", c => Get(c)?.Settings.Detail ?? 5f, 1f, 20f))
                .Value.Subscribe(v => Each(s => s.Detail = v));

            cat.AddControl(new CurrentStateCategorySlider("曲げへの反応", c => Get(c)?.Settings.Sensitivity ?? 8f, 1f, 30f))
                .Value.Subscribe(v => Each(s => s.Sensitivity = v));

            cat.AddControl(new CurrentStateCategorySlider("小さな縮みを無視", c => Get(c)?.Settings.Threshold ?? 0.03f, 0f, 0.3f))
                .Value.Subscribe(v => Each(s => s.Threshold = v));

            cat.AddControl(new CurrentStateCategorySlider("折り目の鋭さ", c => Get(c)?.Settings.Sharpness ?? 0.5f, 0f, 1f))
                .Value.Subscribe(v => Each(s => s.Sharpness = v));

            cat.AddControl(new CurrentStateCategorySlider("しわ模様", c => Get(c)?.Settings.Seed ?? 0, 0f, 100f))
                .Value.Subscribe(v => Each(s => s.Seed = Mathf.RoundToInt(v)));

            cat.AddControl(new CurrentStateCategorySwitch("リアルタイム更新", c => Get(c)?.Settings.Realtime ?? true))
                .Value.Subscribe(v => Each(s => s.Realtime = v));

            // ボタン代わりのスイッチ (切り替えるたびに実行)
            cat.AddControl(new CurrentStateCategorySwitch("今のポーズで更新", c => false))
                .Value.Subscribe(_ => { foreach (var ctrl in StudioAPI.GetSelectedControllers<WrinkleController>()) ctrl.ForceUpdate(); });

            cat.AddControl(new CurrentStateCategorySwitch("今のポーズを基準にする(通常は自動)", c => Get(c)?.HasReference ?? false))
                .Value.Subscribe(v => { foreach (var ctrl in StudioAPI.GetSelectedControllers<WrinkleController>()) ctrl.SetReference(v); });

            var parts = StudioAPI.GetOrCreateCurrentStateCategory("服のしわ (対象)");
            for (int i = 0; i < WrinkleSettings.SlotNames.Length; i++)
            {
                int slot = i;
                parts.AddControl(new CurrentStateCategorySwitch(WrinkleSettings.SlotNames[i], c => Get(c)?.Settings.HasSlot(slot) ?? true))
                    .Value.Subscribe(v => Each(s => s.SetSlot(slot, v)));
            }
        }

        private static WrinkleController Get(OCIChar c) =>
            c?.charInfo != null ? c.charInfo.GetComponent<WrinkleController>() : null;

        private static void Each(Action<WrinkleSettings> change)
        {
            foreach (var ctrl in StudioAPI.GetSelectedControllers<WrinkleController>())
                ctrl.Modify(change);
        }
    }
}
