﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.Config;
using Migration.Common.Log;
using Migration.WIContract;

namespace Migration.Common
{
    public class MigrationContext
    {
        public static MigrationContext Instance { get; private set; }
        public string AttachmentsPath { get; }
        public string SprintsPath { get; }
        public string UserMappingPath { get; }
        public Dictionary<string, string> UserMapping { get; }
        public string App { get; internal set; }
        public string MigrationWorkspace { get; internal set; }
        public LogLevel LogLevel { get; internal set; }
        public bool ForceFresh { get; internal set; }
        public Journal Journal { get; internal set; }
        public WiItemProvider Provider { get; private set; }

        private MigrationContext(string app, ConfigJson config, string logLevel, bool forceFresh)
        {
            App = app;
            MigrationWorkspace = config.Workspace;
            UserMappingPath = config.UserMappingFile != null ? Path.Combine(MigrationWorkspace, config.UserMappingFile) : string.Empty;
            AttachmentsPath = Path.Combine(MigrationWorkspace, config.AttachmentsFolder);
            SprintsPath = Path.Combine(MigrationWorkspace, config.SprintsFolder);
            UserMapping = UserMapper.ParseUserMappings(UserMappingPath);
            LogLevel = Logger.GetLogLevelFromString(logLevel);
            ForceFresh = forceFresh;
        }

        public static MigrationContext Init(string app, ConfigJson config, string logLevel, bool forceFresh, string continueOnCritical)
        {
            Instance = new MigrationContext(app, config, logLevel, forceFresh);

            Logger.Init(app, config.Workspace, logLevel, continueOnCritical);

            Instance.Journal = Journal.Init(Instance);
            Instance.Provider = new WiItemProvider(Instance.MigrationWorkspace, Instance.SprintsPath);

            if (!Directory.Exists(Instance.AttachmentsPath))
                Directory.CreateDirectory(Instance.AttachmentsPath);

            return Instance;
        }

        public WiItem GetItem(string originId)
        {
            var item = Provider.Load(originId);
            item.WiId = Journal.GetMigratedId(originId);
            foreach (var link in item.Revisions.SelectMany(r => r.Links))
            {
                link.SourceOriginId = item.OriginId;
                link.SourceWiId = Journal.GetMigratedId(originId);
            }

            return item;
        }

        public IEnumerable<WiItem> EnumerateAllItems()
        {
            var result = new List<WiItem>();

            foreach (var item in Provider.EnumerateAllItems())
            {
                item.WiId = Journal.GetMigratedId(item.OriginId);
                result.Add(item);
            }

            return result;
        }

        public WiIteration GetIteration(string iterationName)
            => Provider.LoadIteration(iterationName);
    }
}
