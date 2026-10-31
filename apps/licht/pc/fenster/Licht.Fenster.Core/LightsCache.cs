using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Licht.Fenster
{
    /// <summary>
    /// The lamps as last seen (data folder, fenster-lampen.json), so the window shows them at once on the next
    /// start – before Licht has loaded its lamp data. Only a convenience: errors are ignored.
    /// </summary>
    public sealed class LightsCache
    {
        private readonly string _file;

        public LightsCache(string dataDir) => _file = Path.Combine(dataDir, "fenster-lampen.json");

        public List<Light> Load()
        {
            try
            {
                if (!File.Exists(_file)) return new List<Light>();
                return (Json.Parse(File.ReadAllText(_file)) as IList<object?> ?? new List<object?>())
                    .OfType<IDictionary<string, object?>>()
                    .Select(Light.FromJson)
                    .Where(l => l.Id.Length > 0)
                    .Select(l =>
                    {
                        // Not known yet whether it is reachable: Licht reports that in a moment.
                        if (l.Status == LampStatus.Online) l.Status = LampStatus.Connecting;
                        return l;
                    })
                    .ToList();
            }
            catch (Exception)
            {
                return new List<Light>();
            }
        }

        public void Save(IEnumerable<Light> lights)
        {
            try
            {
                var temp = _file + ".neu";
                File.WriteAllText(temp, Json.Write(lights.Select(l => (object?)l.ToJson()).ToList()));
                if (File.Exists(_file)) File.Replace(temp, _file, null);
                else File.Move(temp, _file);
            }
            catch (Exception)
            {
                // only a convenience
            }
        }
    }
}
