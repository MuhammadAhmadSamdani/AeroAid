using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlanner.AeroAid.Vision
{
    /// <summary>One configured detection model (ONNX file).</summary>
    public sealed class ModelConfig
    {
        public string Path { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>Optional per-model confidence threshold (0 = use the global threshold).</summary>
        public double Threshold { get; set; }

        [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>
    /// AI detection preferences (AeroSettings "ai.*"). Values from the earlier fire-only version ("fire.*")
    /// are used as defaults, so existing installations keep their configuration.
    /// </summary>
    public static class AiSettings
    {
        public static string ModelsDirectory => System.IO.Path.Combine(Settings.GetUserDataDirectory(), "AeroAid", "models");

        public static string DefaultFireModelPath => System.IO.Path.Combine(ModelsDirectory, "fire_smoke.onnx");

        public static string DetectionsDirectory =>
            System.IO.Path.Combine(Settings.GetUserDataDirectory(), "AeroAid", "detections");

        private static bool Legacy(string key, bool def) => AeroSettings.GetBool("fire." + key, def);

        public static bool Enabled
        {
            get => AeroSettings.GetBool("ai.enabled", Legacy("enabled", true));
            set => AeroSettings.SetBool("ai.enabled", value);
        }

        /// <summary>Configured models. Default: the fire/smoke model from the previous version.</summary>
        public static List<ModelConfig> Models
        {
            get
            {
                var json = AeroSettings.GetString("ai.models", "");
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var list = JsonConvert.DeserializeObject<List<ModelConfig>>(json);
                        if (list != null) return list.Where(m => !string.IsNullOrWhiteSpace(m.Path)).ToList();
                    }
                    catch (Exception ex)
                    {
                        log4net.LogManager.GetLogger(typeof(AiSettings)).Error("Invalid ai.models setting", ex);
                    }
                }

                var legacy = AeroSettings.GetString("fire.model", "");
                return new List<ModelConfig> { new ModelConfig { Path = string.IsNullOrWhiteSpace(legacy) ? DefaultFireModelPath : legacy } };
            }
            set => AeroSettings.SetString("ai.models", JsonConvert.SerializeObject(value ?? new List<ModelConfig>()));
        }

        public static double ConfidenceThreshold
        {
            get => AeroSettings.GetDouble("ai.threshold", AeroSettings.GetDouble("fire.threshold", 0.5, 0.1, 0.95), 0.1, 0.95);
            set => AeroSettings.SetDouble("ai.threshold", value);
        }

        public static double AnalysisFps
        {
            get => AeroSettings.GetDouble("ai.fps", AeroSettings.GetDouble("fire.fps", 3, 0.5, 15), 0.5, 15);
            set => AeroSettings.SetDouble("ai.fps", value);
        }

        /// <summary>Consecutive analysed frames with a category before it alerts.</summary>
        public static int ConfirmFrames
        {
            get => AeroSettings.GetInt("ai.confirm_frames", AeroSettings.GetInt("fire.confirm_frames", 2, 1, 10), 1, 10);
            set => AeroSettings.SetInt("ai.confirm_frames", value);
        }

        /// <summary>Seconds without a category before its alert episode ends (prevents repeated alerts).</summary>
        public static int ClearSeconds
        {
            get => AeroSettings.GetInt("ai.clear_seconds", AeroSettings.GetInt("fire.clear_seconds", 10, 2, 120), 2, 120);
            set => AeroSettings.SetInt("ai.clear_seconds", value);
        }

        public static bool SaveScreenshots
        {
            get => AeroSettings.GetBool("ai.screenshots", Legacy("screenshots", true));
            set => AeroSettings.SetBool("ai.screenshots", value);
        }

        public static bool PlaySound
        {
            get => AeroSettings.GetBool("ai.sound", Legacy("sound", true));
            set => AeroSettings.SetBool("ai.sound", value);
        }

        /// <summary>Categories switched off by the user (filters). Disabled categories are not shown or alerted.</summary>
        public static HashSet<string> DisabledCategories
        {
            get => new HashSet<string>(AeroSettings.GetString("ai.disabled_categories", "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            set => AeroSettings.SetString("ai.disabled_categories", string.Join(",", value ?? new HashSet<string>()));
        }

        public static bool IsCategoryEnabled(string id) => !DisabledCategories.Contains(id);
    }
}
