using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net;
using Newtonsoft.Json.Linq;

namespace MissionPlanner.AeroAid.Vision
{
    /// <summary>
    /// A detection category (fire, injured person, flood ...). Categories are data, not code paths: the UI,
    /// filters, alerts and history are all generated from the registry, so new categories need no page redesign.
    /// </summary>
    public sealed class DetectionCategory
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string AlertTitle { get; set; }
        public Color Color { get; set; }
        public string Glyph { get; set; }

        /// <summary>Lower-case fragments matched against model class names (e.g. "flame" -> fire).</summary>
        public string[] Keywords { get; set; } = new string[0];

        public bool Builtin { get; set; }
    }

    /// <summary>
    /// Registry of detection categories. Built-ins below; extra categories can be added without code changes in
    /// "&lt;models folder&gt;/categories.json":
    /// <code>[{ "id": "vehicle_accident", "name": "Vehicle accident", "color": "#FF8800", "keywords": ["crash","accident"] }]</code>
    /// </summary>
    public static class DetectionCategories
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string Fire = "fire";
        public const string InjuredPerson = "injured_person";
        public const string Flood = "flood";
        public const string DamagedBuilding = "damaged_building";
        public const string Smoke = "smoke";
        public const string OtherEmergency = "other_emergency";

        private static List<DetectionCategory> all;

        public static IReadOnlyList<DetectionCategory> All
        {
            get
            {
                if (all == null) Load();
                return all;
            }
        }

        private static DetectionCategory B(string id, string name, string alert, string color, string glyph, params string[] keywords) =>
            new DetectionCategory
            {
                Id = id, Name = name, AlertTitle = alert, Color = ColorTranslator.FromHtml(color), Glyph = glyph,
                Keywords = keywords, Builtin = true
            };

        public static void Load()
        {
            // order matters for keyword matching: more specific categories first
            var list = new List<DetectionCategory>
            {
                B(Smoke, "Smoke", "Smoke detected", "#C8C8C8", "", "smoke"),
                B(Fire, "Fire", "Fire detected", "#E5534B", "", "fire", "flame", "blaze", "wildfire"),
                B(InjuredPerson, "Injured person", "Injured person detected", "#F08A24", "",
                    "injured", "casualty", "victim", "wounded", "person_down", "person-down", "fallen_person", "lying_person"),
                B(Flood, "Flood", "Flood detected", "#3FA7E0", "", "flood", "inundat", "standing_water", "submerged"),
                B(DamagedBuilding, "Damaged building", "Damaged building detected", "#B07CFF", "",
                    "damaged", "collapse", "rubble", "debris", "destroyed", "building_damage"),
                B(OtherEmergency, "Other emergency", "Emergency situation detected", "#D6A928", "",
                    "emergency", "accident", "crash", "distress", "hazard", "sos")
            };

            try
            {
                var path = Path.Combine(AiSettings.ModelsDirectory, "categories.json");
                if (File.Exists(path))
                {
                    foreach (var o in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                    {
                        var id = ((string)o["id"] ?? "").Trim().ToLowerInvariant();
                        if (id.Length == 0 || list.Any(c => c.Id == id)) continue;
                        var name = (string)o["name"] ?? id;
                        list.Insert(list.Count - 1, new DetectionCategory
                        {
                            Id = id,
                            Name = name,
                            AlertTitle = (string)o["alert"] ?? name + " detected",
                            Color = TryColor((string)o["color"]),
                            Glyph = "",
                            Keywords = (o["keywords"] as JArray)?.Select(k => ((string)k).ToLowerInvariant()).ToArray() ?? new[] { id }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("categories.json could not be read - using built-in categories only", ex);
            }

            all = list;
        }

        private static Color TryColor(string s)
        {
            try
            {
                return string.IsNullOrEmpty(s) ? Color.FromArgb(0xD6, 0xA9, 0x28) : ColorTranslator.FromHtml(s);
            }
            catch (Exception)
            {
                return Color.FromArgb(0xD6, 0xA9, 0x28);
            }
        }

        public static DetectionCategory Get(string id) => All.FirstOrDefault(c => c.Id == id);

        /// <summary>Category for a model class name, or null if the class is not an emergency category (ignored).</summary>
        public static DetectionCategory Match(string label)
        {
            var l = (label ?? "").ToLowerInvariant();
            if (l.Length == 0) return null;
            var exact = All.FirstOrDefault(c => c.Id == l);
            if (exact != null) return exact;
            return All.FirstOrDefault(c => c.Keywords.Any(k => l.Contains(k)));
        }
    }
}
