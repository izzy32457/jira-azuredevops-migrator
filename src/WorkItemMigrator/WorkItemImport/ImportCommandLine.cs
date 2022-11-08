using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Common.Config;

using Microsoft.Extensions.CommandLineUtils;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Migration.Common;
using Migration.Common.Config;
using Migration.Common.Log;

namespace WorkItemImport
{
    public class ImportCommandLine
    {
        private CommandLineApplication _commandLineApplication;
        private string[] _args;

        public ImportCommandLine(params string[] args)
        {
            InitCommandLine(args);
        }

        public void Run()
        {
            _commandLineApplication.Execute(_args);
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
            _commandLineApplication.Name = "wi-import";

            var tokenOption = _commandLineApplication.Option("--token <accesstoken>", "Personal access token to use for authentication", CommandOptionType.SingleValue);
            var urlOption = _commandLineApplication.Option("--url <accounturl>", "Url for the account", CommandOptionType.SingleValue);
            var configOption = _commandLineApplication.Option("--config <configurationfilename>", "Import the work items based on the configuration file", CommandOptionType.SingleValue);
            var forceOption = _commandLineApplication.Option("--force", "Forces execution from start (instead of continuing from previous run)", CommandOptionType.NoValue);
            var continueOnCriticalOption = _commandLineApplication.Option("--continue", "Continue execution upon a critical error", CommandOptionType.SingleValue);


            _commandLineApplication.OnExecute(() =>
            {
                var forceFresh = forceOption.HasValue();

                if (configOption.HasValue())
                {
                    ExecuteMigration(tokenOption, urlOption, configOption, forceFresh, continueOnCriticalOption);
                }
                else
                {
                    _commandLineApplication.ShowHelp();
                }

                return 0;
            });
        }

        private void ExecuteMigration(CommandOption token, CommandOption url, CommandOption configFile, bool forceFresh, CommandOption continueOnCritical)
        {
            var itemCount = 0;
            var revisionCount = 0;
            var importedItems = 0;
            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var configFileName = configFile.Value();
                var configReaderJson = new ConfigReaderJson(configFileName);
                var config = configReaderJson.Deserialize();

                var context = MigrationContext.Init("wi-import", config, config.LogLevel, forceFresh, continueOnCritical.Value());

                // connection settings for Azure DevOps/TFS:
                // full base url incl https, name of the project where the items will be migrated (if it doesn't exist on destination it will be created), personal access token
                var settings = new Settings(url.Value(), config.TargetProject, token.Value())
                {
                    BaseAreaPath = config.BaseAreaPath ?? string.Empty, // Root area path that will prefix area path of each migrated item
                    BaseIterationPath = config.BaseIterationPath ?? string.Empty, // Root iteration path that will prefix each iteration
                    IgnoreFailedLinks = config.IgnoreFailedLinks,
                    ProcessTemplate = config.ProcessTemplate
                };

                // initialize Azure DevOps/TFS connection. Creates/fetches project, fills area and iteration caches.
                var agent = Agent.Initialize(context, settings);

                if (agent == null)
                {
                    Logger.Log(LogLevel.Critical, "Azure DevOps/TFS initialization error.");
                    return;
                }

                var executionBuilder = new ExecutionPlanBuilder(context);
                var plan = executionBuilder.BuildExecutionPlan();

                itemCount = plan.ReferenceQueue.AsEnumerable()?.Select(x => x.OriginId).Distinct().Count() ?? 0;
                revisionCount = plan.ReferenceQueue?.Count ?? 0;

                BeginSession(configFileName, config, forceFresh, agent, itemCount, revisionCount);

                while (plan.TryPop(out var executionItem))
                {
                    try
                    {
                        if (!forceFresh && context.Journal.IsItemMigrated(executionItem.OriginId, executionItem.Revision.Index))
                            continue;
                        
                        var isFirstRun = true;
                        WorkItem wi;
                        do
                        {
                            if (!isFirstRun)
                            {
                                Thread.Sleep(TimeSpan.FromSeconds(1));
                            }

                            wi = executionItem.WiId > 0
                                ? agent.GetWorkItem(executionItem.WiId)
                                : agent.CreateWorkItem(executionItem.WiType);
                            isFirstRun = false;
                        } while (wi is null);

                        Logger.Log(LogLevel.Info, $"Processing {importedItems + 1}/{revisionCount} - wi '{(wi.Id > 0 ? wi.Id.ToString() : "Initial revision")}', jira '{executionItem.OriginId}, rev {executionItem.Revision.Index}'.");

                        agent.ImportRevision(executionItem.Revision, wi);
                        importedItems++;
                    }
                    catch (AbortMigrationException)
                    {
                        Logger.Log(LogLevel.Info, "Aborting migration...");
                        break;
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(ex, $"Failed to import '{executionItem}'.");
                        }
                        catch (AbortMigrationException)
                        {
                            break;
                        }
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
                EndSession(itemCount, revisionCount, sw);
            }
        }

        private static void BeginSession(string configFile, ConfigJson config, bool force, Agent agent, int itemsCount, int revisionCount)
        {
            var toolVersion = VersionInfo.GetVersionInfo();
            var osVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
            var machine = Environment.MachineName;
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var hostingType = GetHostingType(agent);

            Logger.Log(LogLevel.Info, $"Import started. Importing {itemsCount} items with {revisionCount} revisions.");

            Logger.StartSession("Azure DevOps Work Item Import",
                "wi-import-started",
                new Dictionary<string, string>
                {
                    { "Tool version         :", toolVersion },
                    { "Start time           :", DateTime.Now.ToString(CultureInfo.InvariantCulture) },
                    { "Telemetry            :", Logger.TelemetryStatus },
                    { "Session id           :", Logger.SessionId },
                    { "Tool user            :", user },
                    { "Config               :", configFile },
                    { "User                 :", user },
                    { "Force                :", force ? "yes" : "no" },
                    { "Log level            :", config.LogLevel },
                    { "Machine              :", machine },
                    { "System               :", osVersion },
                    { "Azure DevOps url     :", agent.Settings.Account },
                    { "Azure DevOps version :", "n/a" },
                    { "Azure DevOps type    :", hostingType }
                    },
                new Dictionary<string, string>
                {
                    { "item-count", itemsCount.ToString() },
                    { "revision-count", revisionCount.ToString() },
                    { "system-version", "n/a" },
                    { "hosting-type", hostingType } });
        }

        private static string GetHostingType(Agent agent)
        {
            var uri = new Uri(agent.Settings.Account);
            switch (uri.Host.ToLower())
            {
                case "dev.azure.com":
                case "visualstudio.com":
                    return "Cloud";
                default:
                    return "Server";
            }
        }

        private static void EndSession(int itemsCount, int revisionCount, Stopwatch sw)
        {
            sw.Stop();

            // ReSharper disable once UseStringInterpolation - this causes issues here for some reason
            Logger.Log(LogLevel.Info, $"Import complete. Imported {itemsCount} items, {revisionCount} revisions ({Logger.Errors} errors, {Logger.Warnings} warnings) in {string.Format("{0:hh\\:mm\\:ss}", sw.Elapsed)}.");

            Logger.EndSession("wi-import-completed",
                new Dictionary<string, string>
                {
                    { "item-count", itemsCount.ToString() },
                    { "revision-count", revisionCount.ToString() },
                    { "error-count", Logger.Errors.ToString() },
                    { "warning-count", Logger.Warnings.ToString() },
                    // ReSharper disable once UseStringInterpolation - this causes issues here for some reason
                    { "elapsed-time", string.Format("{0:hh\\:mm\\:ss}", sw.Elapsed) } });
        }
    }
}