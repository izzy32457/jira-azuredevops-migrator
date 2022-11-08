using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Common.Config;

using Microsoft.Extensions.CommandLineUtils;

using Migration.Common.Config;
using Migration.Common.Log;
using Migration.WIContract;
using static JiraExport.JiraProvider;

namespace JiraExport
{
    public class JiraCommandLine
    {
        private CommandLineApplication _commandLineApplication;
        private string[] _args;

        public JiraCommandLine(params string[] args)
        {
            InitCommandLine(args);
        }

        private void InitCommandLine(params string[] args)
        {
            _commandLineApplication = new CommandLineApplication(throwOnUnexpectedArg: true);
            this._args = args;
            ConfigureCommandLineParserWithOptions();
        }

        private void ConfigureCommandLineParserWithOptions()
        {
            _commandLineApplication.HelpOption("-? | -h | --help");
            _commandLineApplication.FullName = "Work item migration tool that assists with moving Jira items to Azure DevOps or TFS.";
            _commandLineApplication.Name = "jira-export";

            var userOption = _commandLineApplication.Option("-u <username>", "Username for authentication", CommandOptionType.SingleValue);
            var passwordOption = _commandLineApplication.Option("-p <password>", "Password for authentication", CommandOptionType.SingleValue);
            var urlOption = _commandLineApplication.Option("--url <accounturl>", "Url for the account", CommandOptionType.SingleValue);
            var configOption = _commandLineApplication.Option("--config <configurationfilename>", "Export the work items based on this configuration file", CommandOptionType.SingleValue);
            var forceOption = _commandLineApplication.Option("--force", "Forces execution from start (instead of continuing from previous run)", CommandOptionType.NoValue);
            var continueOnCriticalOption = _commandLineApplication.Option("--continue", "Continue execution upon a critical error", CommandOptionType.SingleValue);

            _commandLineApplication.OnExecute(() =>
            {
                var forceFresh = forceOption.HasValue();

                if (configOption.HasValue())
                {
                    ExecuteMigration(userOption, passwordOption, urlOption, configOption, forceFresh, continueOnCriticalOption);
                }
                else
                {
                    _commandLineApplication.ShowHelp();
                }

                return 0;
            });
        }

        private static void ExecuteMigration(CommandOption user, CommandOption password, CommandOption url, CommandOption configFile, bool forceFresh, CommandOption continueOnCritical)
        {
            var itemsCount = 0;
            var exportedItemsCount = 0;
            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var configFileName = configFile.Value();
                var configReaderJson = new ConfigReaderJson(configFileName);
                var config = configReaderJson.Deserialize();

                InitSession(config, continueOnCritical.Value());

                // Migration session level settings
                // where the logs and journal will be saved, logs aid debugging, journal is for recovery of interrupted process
                var migrationWorkspace = config.Workspace;

                var downloadOptions = (DownloadOptions)config.DownloadOptions;

                var jiraSettings = new JiraSettings(user.Value(), password.Value(), url.Value(), config.SourceProject)
                {
                    BatchSize = config.BatchSize,
                    UserMappingFile = config.UserMappingFile != null ? Path.Combine(migrationWorkspace, config.UserMappingFile) : string.Empty,
                    AttachmentsDir = Path.Combine(migrationWorkspace, config.AttachmentsFolder),
                    SprintsDir = Path.Combine(migrationWorkspace, config.SprintsFolder),
                    JQL = config.Query,
                    UsingJiraCloud = config.UsingJiraCloud,
                    BoardID = config.SourceBoardId
                };

                var jiraServiceWrapper = new JiraServiceWrapper(jiraSettings);
                var jiraProvider = new JiraProvider(jiraServiceWrapper);
                jiraProvider.Initialize(jiraSettings);

                itemsCount = jiraProvider.GetItemCount(jiraSettings.JQL);

                BeginSession(configFileName, config, forceFresh, jiraProvider, itemsCount);

                jiraSettings.EpicLinkField = jiraProvider.GetCustomId(config.EpicLinkField);
                if (string.IsNullOrEmpty(jiraSettings.EpicLinkField))
                {
                    Logger.Log(LogLevel.Warning, $"Epic link field missing for config field '{config.EpicLinkField}'.");
                }
                jiraSettings.SprintField = jiraProvider.GetCustomId(config.SprintField);
                if (string.IsNullOrEmpty(jiraSettings.SprintField))
                {
                    Logger.Log(LogLevel.Warning, $"Sprint link field missing for config field '{config.SprintField}'.");
                }

                var mapper = new JiraMapper(jiraProvider, config);
                var localProvider = new WiItemProvider(migrationWorkspace, jiraSettings.SprintsDir);
                var exportedKeys = new HashSet<string>(Directory.EnumerateFiles(migrationWorkspace, "*.json").Select(Path.GetFileNameWithoutExtension));
                var skips = forceFresh ? new HashSet<string>(Enumerable.Empty<string>()) : exportedKeys;
                var exportedSprints = new HashSet<string>(Directory.EnumerateFiles(Path.Combine(migrationWorkspace, "sprints"), "*.json").Select(Path.GetFileNameWithoutExtension));
                var skipSprints = forceFresh ? new HashSet<string>(Enumerable.Empty<string>()) : exportedSprints;

                foreach (var sprint in jiraProvider.EnumerateSprints(skipSprints))
                {
                    var iteration = mapper.Map(sprint);
                    if (iteration != null)
                    {
                        localProvider.Save(iteration);
                        exportedItemsCount++;
                        Logger.Log(LogLevel.Debug, $"Exported sprint '{iteration.Name}'.");
                    }
                }

                foreach (var issue in jiraProvider.EnumerateIssues(jiraSettings.JQL, skips, downloadOptions))
                {
                    if (issue == null)
                        continue;

                    var wiItem = mapper.Map(issue);
                    if (wiItem != null)
                    {
                        localProvider.Save(wiItem);
                        exportedItemsCount++;
                        Logger.Log(LogLevel.Debug, $"Exported as type '{wiItem.Type}'.");
                    }
                }
            }
            catch (CommandParsingException e)
            {
                Logger.Log(LogLevel.Error, $"Invalid command line option(s): {e}");
            }
            catch (Exception e)
            {
                Logger.Log(e, "Unexpected migration error.");
            }
            finally
            {
                EndSession(itemsCount, exportedItemsCount, sw);
            }
        }

        private static void InitSession(ConfigJson config, string continueOnCritical)
        {
            Logger.Init("jira-export", config.Workspace, config.LogLevel, continueOnCritical);

            var sprintsFolder = Path.Combine(config.Workspace, config.SprintsFolder);
            if (!Directory.Exists(sprintsFolder))
            {
                Directory.CreateDirectory(sprintsFolder);
            }
        }

        private static void BeginSession(string configFile, ConfigJson config, bool force, JiraProvider jiraProvider, int itemsCount)
        {
            var toolVersion = VersionInfo.GetVersionInfo();
            var osVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
            var machine = Environment.MachineName;
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var jiraVersion = jiraProvider.GetJiraVersion();

            Logger.Log(LogLevel.Info, $"Export started. Exporting {itemsCount} items.");

            Logger.StartSession("Jira Export",
                "jira-export-started",
                new Dictionary<string, string>
                {
                    { "Tool version :", toolVersion },
                    { "Start time   :", DateTime.Now.ToString(CultureInfo.InvariantCulture) },
                    { "Telemetry    :", Logger.TelemetryStatus },
                    { "Session id   :", Logger.SessionId },
                    { "Tool user    :", user },
                    { "Config       :", configFile },
                    { "Force        :", force ? "yes" : "no" },
                    { "Log level    :", config.LogLevel },
                    { "Machine      :", machine },
                    { "System       :", osVersion },
                    { "Jira url     :", jiraProvider.Settings.Url },
                    { "Jira user    :", jiraProvider.Settings.UserID },
                    { "Jira version :", jiraVersion.Version },
                    { "Jira type    :", jiraVersion.DeploymentType }
                    },
                new Dictionary<string, string>
                {
                    { "item-count", itemsCount.ToString() },
                    { "system-version", jiraVersion.Version },
                    { "hosting-type", jiraVersion.DeploymentType } });
        }

        private static void EndSession(int itemsCount, int exportedItemsCount, Stopwatch sw)
        {
            sw.Stop();

            Logger.Log(LogLevel.Info, $"Export complete. Exported {itemsCount} items ({Logger.Errors} errors, {Logger.Warnings} warnings) in {sw.Elapsed:hh\\:mm\\:ss}.");

            Logger.EndSession("jira-export-completed",
                new Dictionary<string, string>
                {
                    { "item-count", itemsCount.ToString() },
                    { "exported-item-count", exportedItemsCount.ToString() },
                    { "error-count", Logger.Errors.ToString() },
                    { "warning-count", Logger.Warnings.ToString() },
                    { "elapsed-time", $"{sw.Elapsed:hh\\:mm\\:ss}"}});
        }

        public void Run()
        {
            _commandLineApplication.Execute(_args);
        }
    }
}