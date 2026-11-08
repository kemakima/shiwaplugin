using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKAPI;
using KKAPI.Chara;
using KKAPI.Studio;

[assembly: AssemblyTitle("KKS_ClothWrinkle")]
[assembly: AssemblyVersion(ClothWrinkle.WrinklePlugin.Version)]

namespace ClothWrinkle
{
    /// <summary>
    /// 体を曲げたときに服 (レオタードなど) が縮んだ所へ、横じわを足すプラグイン。
    /// 今のポーズのメッシュと元の形を三角形ごとに比べ、体の軸方向に縮んだ所ほど濃く、
    /// 法線マップ (陰影) と、KK 系シェーダーの描き込みマスク (アニメ調の線・影) にしわを描く。
    /// </summary>
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInDependency(KoikatuAPI.GUID, "1.30")]
    public class WrinklePlugin : BaseUnityPlugin
    {
        public const string GUID = "zuki.kks.clothwrinkle";
        public const string PluginName = "ClothWrinkle (服のしわ)";
        public const string Version = "0.5.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> MapResolution;
        internal static ConfigEntry<int> OutputMax;
        internal static ConfigEntry<bool> DebugLog;
        internal static ConfigEntry<float> MaxRate;
        internal static ConfigEntry<bool> DumpDebug;
        internal static ConfigEntry<bool> MeshDraw;

        private static string _logPath;

        /// <summary>UserData\ClothWrinkle_log.txt に追記 (BepInEx のログファイルが無い環境でも確認できるように)。</summary>
        internal static void FileLog(string msg)
        {
            try
            {
                if (_logPath == null) _logPath = System.IO.Path.Combine(System.IO.Path.Combine(Paths.GameRootPath, "UserData"), "ClothWrinkle_log.txt");
                System.IO.File.AppendAllText(_logPath, System.DateTime.Now.ToString("HH:mm:ss ") + msg + System.Environment.NewLine);
            }
            catch { }
        }

        private void Awake()
        {
            Log = Logger;
            FileLog("---- ClothWrinkle " + Version + " 起動 ----");

            MapResolution = Config.Bind("Quality", "Wrinkle map resolution", 512,
                new ConfigDescription("しわの形を計算する解像度。1024 で細かく、256 で軽く。",
                    new AcceptableValueList<int>(256, 512, 1024)));

            OutputMax = Config.Bind("Quality", "Normal map max size", 1024,
                new ConfigDescription("書き込む法線マップの最大サイズ。",
                    new AcceptableValueList<int>(512, 1024, 2048)));

            MaxRate = Config.Bind("Update", "Max updates per second", 30f,
                new ConfigDescription("リアルタイム更新で、1秒間にしわの濃さを計算し直す最大回数。下げるほど軽い。",
                    new AcceptableValueRange<float>(1f, 60f)));

            MeshDraw = Config.Bind("Update", "Fast mesh drawing", true,
                "しわの合成をメッシュ一括で描く (速い)。起動後最初の描画で動くか自動で確かめ、だめなら従来の GL 描画に切り替える。オフにすると常に GL 描画。");

            DumpDebug = Config.Bind("Debug", "Dump debug textures", false,
                "オンにすると、次の更新で法線マップ (元/しわ最大/結果) を UserData\\ClothWrinkle_debug に PNG で保存し、自動でオフに戻る。");

            DebugLog = Config.Bind("Debug", "Log strain stats", false,
                "更新のたびに、服ごとの最大の縮み・しわ量をログに出す。");
        }

        private void Start()
        {
            CharacterApi.RegisterExtraBehaviour<WrinkleController>(GUID);

            if (StudioAPI.InsideStudio)
                StudioUI.Register();
        }
    }
}
