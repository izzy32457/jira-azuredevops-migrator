using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Migration.Common.Log;
using Newtonsoft.Json;

namespace Migration.WIContract
{
    public class WiItemProvider
    {
        private readonly string _itemsDir;
        private readonly string _sprintsDir;

        public WiItemProvider(string itemsDir, string sprintsDir)
        {
            _itemsDir = itemsDir;
            _sprintsDir = sprintsDir;
        }

        public WiItem Load(string originId)
        {
            var path = Path.Combine(_itemsDir, $"{originId}.json");
            return LoadFile(path);
        }

        public WiIteration LoadIteration(string iterationId)
        {
            var path = Path.Combine(_sprintsDir, $"{iterationId}.json");
            return LoadFile<WiIteration>(path);
        }

        private static WiItem LoadFile(string path)
        {
            var deserialized = LoadFile<WiItem>(path);

            foreach (var rev in deserialized.Revisions)
                rev.ParentOriginId = deserialized.OriginId;

            return deserialized;
        }

        private static T LoadFile<T>(string path)
        {
            var serialized = File.ReadAllText(path);

            if (Regex.Matches(serialized, @"\\u[0-F]{4}").Count > 0)
            {
                Logger.Log(LogLevel.Warning, "Detected unicode characters, removed.");
                serialized = Regex.Replace(serialized, @"\\u[0-F]{4}", "");
            }
            
            return JsonConvert.DeserializeObject<T>(serialized, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        public void Save(WiItem item)
        {
            var path = Path.Combine(_itemsDir, $"{item.OriginId}.json");
            Save(item, path);
        }

        public void Save(WiIteration item)
        {
            var path = Path.Combine(_sprintsDir, $"{item.Name}.json");
            Save(item, path);
        }

        private static void Save<TWiType>(TWiType item, string path)
        {
            var serialized = JsonConvert.SerializeObject(item, Formatting.Indented);
            File.WriteAllText(path, serialized);
        }

        public IEnumerable<WiItem> EnumerateAllItems()
            => EnumerateAll(_itemsDir, LoadFile);

        public IEnumerable<TWiType> EnumerateAll<TWiType>(string basePath, Func<string, TWiType> loaderFunc)
        {
            var result = new List<TWiType>();

            foreach (var filePath in Directory.EnumerateFiles(basePath, "*.json"))
            {
                try
                {
                    result.Add(loaderFunc(filePath));
                }
                catch (Exception)
                {
                    Logger.Log(LogLevel.Warning, $"Failed to load '{Path.GetFileName(filePath)}' (perhaps not a migration file?).");
                }
            }
            return result;
        }
    }
}