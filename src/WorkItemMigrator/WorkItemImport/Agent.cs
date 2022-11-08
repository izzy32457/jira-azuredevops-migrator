using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Operations;

using Migration.Common;
using Migration.Common.Log;
using Migration.WIContract;

using VsWebApi = Microsoft.VisualStudio.Services.WebApi;
using WebApi = Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using WebModel = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;

using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using System.IO;

namespace WorkItemImport
{
    public class Agent
    {
        private readonly MigrationContext _context;
        public Settings Settings { get; }

        public TfsTeamProjectCollection Collection { get; }

        public VsWebApi.VssConnection RestConnection { get; }
        public Dictionary<string, int> IterationCache { get; private set; } = new Dictionary<string, int>();
        public int RootIteration { get; private set; }
        public Dictionary<string, int> AreaCache { get; private set; } = new Dictionary<string, int>();
        public int RootArea { get; private set; }

        private WitClientUtils _witClientUtils;
        private WebApi.WorkItemTrackingHttpClient _wiClient;
        public WebApi.WorkItemTrackingHttpClient WiClient
            => _wiClient ?? (_wiClient = RestConnection.GetClient<WebApi.WorkItemTrackingHttpClient>());

        private Agent(MigrationContext context, Settings settings, VsWebApi.VssConnection restConn, TfsTeamProjectCollection soapConnection)
        {
            _context = context;
            Settings = settings;
            RestConnection = restConn;
            Collection = soapConnection;
        }

        public WorkItem GetWorkItem(int wiId)
        {
            return _witClientUtils.GetWorkItem(wiId);
        }

        public WorkItem CreateWorkItem(string type)
        {
            return _witClientUtils.CreateWorkItem(type);
        }

        public bool ImportRevision(WiRevision rev, WorkItem wi)
        {
            var incomplete = false;
            try
            {
                if (rev.Index == 0)
                    _witClientUtils.EnsureClassificationFields(rev);

                _witClientUtils.EnsureDateFields(rev, wi);
                _witClientUtils.EnsureAuthorFields(rev);
                _witClientUtils.EnsureAssigneeField(rev, wi);
                _witClientUtils.EnsureFieldsOnStateChange(rev, wi);

                _witClientUtils.EnsureWorkItemFieldsInitialized(rev, wi);

                var attachmentMap = new Dictionary<string, WiAttachment>();
                if (rev.Attachments.Any() && !_witClientUtils.ApplyAttachments(rev, wi, attachmentMap, _context.Journal.IsAttachmentMigrated))
                    incomplete = true;

                if (rev.Fields.Any() && !UpdateWiFields(rev.Fields, wi))
                    incomplete = true;

                if (rev.Fields.Any() && !UpdateWiHistoryField(rev.Fields, wi))
                    incomplete = true;

                if (rev.Links.Any() && !ApplyAndSaveLinks(rev, wi))
                    incomplete = true;

                if (incomplete)
                    Logger.Log(LogLevel.Warning, $"'{rev}' - not all changes were saved.");

                if (rev.Attachments.All(a => a.Change != ReferenceChangeType.Added) && rev.AttachmentReferences)
                {
                    Logger.Log(LogLevel.Debug, $"Correcting description on '{rev}'.");
                    _witClientUtils.CorrectDescription(wi, _context.GetItem(rev.ParentOriginId), rev, _context.Journal.IsAttachmentMigrated);
                }
                if (wi.Fields.ContainsKey(WiFieldReference.History) && !string.IsNullOrEmpty(wi.Fields[WiFieldReference.History].ToString()))
                {
                    Logger.Log(LogLevel.Debug, $"Correcting comments on '{rev}'.");
                    _witClientUtils.CorrectComment(wi, _context.GetItem(rev.ParentOriginId), rev, _context.Journal.IsAttachmentMigrated);
                }

                _witClientUtils.SaveWorkItem(rev, wi);

                foreach (var attOriginId in rev.Attachments.Select(wiAtt => wiAtt.AttOriginId))
                {
                    if (attachmentMap.TryGetValue(attOriginId, out var tfsAtt))
                        _context.Journal.MarkAttachmentAsProcessed(attOriginId, tfsAtt.AttOriginId);
                }

                if (rev.Attachments.Any(a => a.Change == ReferenceChangeType.Added) && rev.AttachmentReferences)
                {
                    Logger.Log(LogLevel.Debug, $"Correcting description on separate revision on '{rev}'.");

                    try
                    {
                        if (_witClientUtils.CorrectDescription(wi, _context.GetItem(rev.ParentOriginId), rev, _context.Journal.IsAttachmentMigrated))
                            _witClientUtils.SaveWorkItem(rev, wi);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"Failed to correct description for '{wi.Id}', rev '{rev}'.");
                    }
                }

                if (wi.Id.HasValue)
                {
                    _context.Journal.MarkRevProcessed(rev.ParentOriginId, wi.Id.Value, rev.Index);
                } else
                {
                    throw new MissingFieldException($"Work Item had no ID: {wi.Url}");
                }

                Logger.Log(LogLevel.Debug, "Imported revision.");

                return true;
            }
            catch (AbortMigrationException)
            {
                throw;
            }
            catch (FileNotFoundException ex)
            {
                Logger.Log(LogLevel.Error, ex.Message);
                Logger.Log(LogLevel.Error, $"Failed to import revision '{rev.Index}' of '{rev.ParentOriginId}'.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Failed to import revisions for '{wi.Id}'.");
                return false;
            }
        }

        #region Static
        internal static Agent Initialize(MigrationContext context, Settings settings)
        {
            var restConnection = EstablishRestConnection(settings);
            if (restConnection == null)
                return null;

            var soapConnection = EstablishSoapConnection(settings);
            if (soapConnection == null)
                return null;

            var agent = new Agent(context, settings, restConnection, soapConnection);

            var witClientWrapper = new WitClientWrapper(settings.Account, settings.Project, settings.Pat);
            agent._witClientUtils = new WitClientUtils(witClientWrapper);

            // check if projects exists, if not create it
            var project = agent.GetOrCreateProjectAsync().Result;
            if (project == null)
            {
                Logger.Log(LogLevel.Critical, "Could not establish connection to the remote Azure DevOps/TFS project.");
                return null;
            }

            var (iterationCache, rootIteration) = agent.CreateClassificationCacheAsync(settings.Project, TreeStructureGroup.Iterations).Result;
            if (iterationCache == null)
            {
                Logger.Log(LogLevel.Critical, "Could not build iteration cache.");
                return null;
            }

            agent.IterationCache = iterationCache;
            agent.RootIteration = rootIteration;

            var (areaCache, rootArea) = agent.CreateClassificationCacheAsync(settings.Project, TreeStructureGroup.Areas).Result;
            if (areaCache == null)
            {
                Logger.Log(LogLevel.Critical, "Could not build area cache.");
                return null;
            }

            agent.AreaCache = areaCache;
            agent.RootArea = rootArea;

            return agent;
        }

        private static VsWebApi.VssConnection EstablishRestConnection(Settings settings)
        {
            try
            {
                Logger.Log(LogLevel.Info, "Connecting to Azure DevOps/TFS...");
                var credentials = new VssBasicCredential("", settings.Pat);
                var uri = new Uri(settings.Account);
                return new VsWebApi.VssConnection(uri, credentials);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Cannot establish connection to Azure DevOps/TFS.", LogLevel.Critical);
                return null;
            }
        }

        private static TfsTeamProjectCollection EstablishSoapConnection(Settings settings)
        {
            var netCred = new NetworkCredential(string.Empty, settings.Pat);
            var basicCred = new VssBasicCredential(netCred);
            var tfsCred = new VssCredentials(basicCred);
            var collection = new TfsTeamProjectCollection(new Uri(settings.Account), tfsCred);
            collection.Authenticate();
            return collection;
        }

        #endregion

        #region Setup

        internal async Task<TeamProject> GetOrCreateProjectAsync()
        {
            var projectClient = RestConnection.GetClient<ProjectHttpClient>();
            Logger.Log(LogLevel.Info, "Retrieving project info from Azure DevOps/TFS...");
            TeamProject project = null;

            try
            {
                project = await projectClient.GetProject(Settings.Project);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Failed to get Azure DevOps/TFS project '{Settings.Project}'.");
            }

            // ReSharper disable once RedundantAssignment
            return project ?? (project = await CreateProject(Settings.Project,
                $"{Settings.ProcessTemplate} project for Jira migration", Settings.ProcessTemplate));
        }

        internal async Task<TeamProject> CreateProject(string projectName, string projectDescription = "", string processName = "Scrum")
        {
            Logger.Log(LogLevel.Warning, $"Project '{projectName}' does not exist.");
            Console.WriteLine("Would you like to create one? (Y/N)");
            var answer = Console.ReadKey();
            if (answer.KeyChar != 'Y' && answer.KeyChar != 'y')
                return null;

            Logger.Log(LogLevel.Info, $"Creating project '{projectName}'.");

            // Setup version control properties
            var versionControlProperties = new Dictionary<string, string>
            {
                [TeamProjectCapabilitiesConstants.VersionControlCapabilityAttributeName] = SourceControlTypes.Git.ToString()
            };

            // Setup process properties       
            var processClient = RestConnection.GetClient<ProcessHttpClient>();
            var processId = processClient.GetProcessesAsync().Result.Find(process => process.Name.Equals(processName, StringComparison.InvariantCultureIgnoreCase)).Id;

            var processProperties = new Dictionary<string, string>
            {
                [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityTemplateTypeIdAttributeName] = processId.ToString()
            };

            // Construct capabilities dictionary
            var capabilities = new Dictionary<string, Dictionary<string, string>>
            {
                [TeamProjectCapabilitiesConstants.VersionControlCapabilityName] = versionControlProperties,
                [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityName] = processProperties
            };

            // Construct object containing properties needed for creating the project
            var projectCreateParameters = new TeamProject
            {
                Name = projectName,
                Description = projectDescription,
                Capabilities = capabilities
            };

            // Get a client
            var projectClient = RestConnection.GetClient<ProjectHttpClient>();

            TeamProject project = null;
            try
            {
                Logger.Log(LogLevel.Info, "Queuing project creation...");

                // Queue the project creation operation 
                // This returns an operation object that can be used to check the status of the creation
                var operation = await projectClient.QueueCreateProject(projectCreateParameters);

                // Check the operation status every 5 seconds (for up to 30 seconds)
                var completedOperation = WaitForLongRunningOperation(operation.Id, 5, 30).Result;

                // Check if the operation succeeded (the project was created) or failed
                if (completedOperation.Status == OperationStatus.Succeeded)
                {
                    // Get the full details about the newly created project
                    project = projectClient.GetProject(
                        projectCreateParameters.Name,
                        includeCapabilities: true,
                        includeHistory: true).Result;

                    Logger.Log(LogLevel.Info, $"Project created (ID: {project.Id})");
                }
                else
                {
                    Logger.Log(LogLevel.Error, "Project creation operation failed: " + completedOperation.ResultMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Exception during create project.", LogLevel.Critical);
            }

            return project;
        }

        private async Task<Operation> WaitForLongRunningOperation(Guid operationId, int intervalInSec = 5, int maxTimeInSeconds = 60, CancellationToken cancellationToken = default)
        {
            var operationsClient = RestConnection.GetClient<OperationsHttpClient>();
            var expiration = DateTime.Now.AddSeconds(maxTimeInSeconds);
            var checkCount = 0;

            while (true)
            {
                Logger.Log(LogLevel.Info, $" Checking status ({checkCount++})... ");

                var operation = await operationsClient.GetOperation(operationId, cancellationToken: cancellationToken);

                if (!operation.Completed)
                {
                    Logger.Log(LogLevel.Info, $"   Pausing {intervalInSec} seconds...");

                    await Task.Delay(intervalInSec * 1000, cancellationToken);

                    if (DateTime.Now > expiration)
                    {
                        Logger.Log(LogLevel.Error, $"Operation did not complete in {maxTimeInSeconds} seconds.");
                    }
                }
                else
                {
                    return operation;
                }
            }
        }

        private async Task<(Dictionary<string, int>, int)> CreateClassificationCacheAsync(string project, TreeStructureGroup structureGroup)
        {
            try
            {
                Logger.Log(LogLevel.Info, $"Building {(structureGroup == TreeStructureGroup.Iterations ? "iteration" : "area")} cache...");
                var all = await WiClient.GetClassificationNodeAsync(project, structureGroup, null, 1000);

                var classificationCache = new Dictionary<string, int>();

                if (all.Children != null && all.Children.Any())
                {
                    foreach (var iteration in all.Children)
                        CreateClassificationCacheRec(iteration, classificationCache, "");
                }

                return (classificationCache, all.Id);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Error while building {(structureGroup == TreeStructureGroup.Iterations ? "iteration" : "area")} cache.");
                return (null, -1);
            }
        }

        private void CreateClassificationCacheRec(WorkItemClassificationNode current, Dictionary<string, int> agg, string parentPath)
        {
            var fullName = !string.IsNullOrWhiteSpace(parentPath) ? parentPath + "/" + current.Name : current.Name;

            agg.Add(fullName, current.Id);
            Logger.Log(LogLevel.Debug, $"{(current.StructureType == TreeNodeStructureType.Iteration ? "Iteration" : "Area")} '{fullName}' added to cache");
            if (current.Children != null)
            {
                foreach (var node in current.Children)
                    CreateClassificationCacheRec(node, agg, fullName);
            }
        }

        public int? EnsureClassification(string fullName, TreeStructureGroup structureGroup = TreeStructureGroup.Iterations)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                Logger.Log(LogLevel.Error, "Empty value provided for node name/path.");
                throw new ArgumentException("fullName");
            }

            var path = fullName.Split('/');
            var name = path.Last();
            var parent = string.Join("/", path.Take(path.Length - 1));

            if (!string.IsNullOrEmpty(parent))
                EnsureClassification(parent, structureGroup);

            var cache = structureGroup == TreeStructureGroup.Iterations ? IterationCache : AreaCache;

            lock (cache)
            {
                if (cache.TryGetValue(fullName, out var id))
                    return id;

                WorkItemClassificationNode node = null;

                try
                {
                    node = WiClient.CreateOrUpdateClassificationNodeAsync(
                        new WorkItemClassificationNode
                        {
                            Name = name,
                            StructureType = TreeNodeStructureType.Iteration,
                            Attributes = GetIterationAttributes(fullName)
                        },
                        Settings.Project,
                        structureGroup,
                        parent)
                        .Result;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Error while adding {(structureGroup == TreeStructureGroup.Iterations ? "iteration" : "area")} '{fullName}' to Azure DevOps/TFS.", LogLevel.Critical);
                }

                if (node != null)
                {
                    Logger.Log(LogLevel.Debug, $"{(structureGroup == TreeStructureGroup.Iterations ? "Iteration" : "Area")} '{fullName}' added to Azure DevOps/TFS.");
                    cache.Add(fullName, node.Id);
                    return node.Id;
                }
            }
            return null;
        }

        #endregion

        #region Import Revision

        private static bool UpdateWiHistoryField(IEnumerable<WiField> fields, WorkItem wi)
        {
            if(fields.FirstOrDefault( i => i.ReferenceName == WiFieldReference.History ) == null )
            {
                wi.Fields.Remove(WiFieldReference.History);
            }
            return true;
        }

        private bool UpdateWiFields(IEnumerable<WiField> fields, WorkItem wi)
        {
            var success = true;

            foreach (var fieldRev in fields)
            {
                try
                {
                    var fieldRef = fieldRev.ReferenceName;
                    var fieldValue = fieldRev.Value;


                    switch (fieldRef)
                    {
                        case var s when s.Equals(WiFieldReference.IterationPath, StringComparison.InvariantCultureIgnoreCase):

                            var iterationPath = Settings.BaseIterationPath;

                            if (!string.IsNullOrWhiteSpace((string)fieldValue))
                            {
                                if (string.IsNullOrWhiteSpace(iterationPath))
                                    iterationPath = (string)fieldValue;
                                else
                                    iterationPath = string.Join("/", iterationPath, (string)fieldValue);
                            }

                            if (!string.IsNullOrWhiteSpace(iterationPath))
                            {
                                EnsureClassification(iterationPath);
                                wi.Fields[WiFieldReference.IterationPath] = $@"{Settings.Project}\{iterationPath}".Replace("/", @"\");
                            }
                            else
                            {
                                wi.Fields[WiFieldReference.IterationPath] = Settings.Project;
                            }
                            Logger.Log(LogLevel.Debug, $"Mapped IterationPath '{wi.Fields[WiFieldReference.IterationPath]}'.");
                            break;

                        case var s when s.Equals(WiFieldReference.AreaPath, StringComparison.InvariantCultureIgnoreCase):

                            var areaPath = Settings.BaseAreaPath;

                            if (!string.IsNullOrWhiteSpace((string)fieldValue))
                            {
                                if (string.IsNullOrWhiteSpace(areaPath))
                                    areaPath = (string)fieldValue;
                                else
                                    areaPath = string.Join("/", areaPath, (string)fieldValue);
                            }

                            if (!string.IsNullOrWhiteSpace(areaPath))
                            {
                                EnsureClassification(areaPath, TreeStructureGroup.Areas);
                                wi.Fields[WiFieldReference.AreaPath] = $@"{Settings.Project}\{areaPath}".Replace("/", @"\");
                            }
                            else
                            {
                                wi.Fields[WiFieldReference.AreaPath] = Settings.Project;
                            }

                            Logger.Log(LogLevel.Debug, $"Mapped AreaPath '{ wi.Fields[WiFieldReference.AreaPath] }'.");

                            break;

                        case var s when s.Equals(WiFieldReference.ActivatedDate, StringComparison.InvariantCultureIgnoreCase) && fieldValue == null ||
                             s.Equals(WiFieldReference.ActivatedBy, StringComparison.InvariantCultureIgnoreCase) && fieldValue == null ||
                            s.Equals(WiFieldReference.ClosedDate, StringComparison.InvariantCultureIgnoreCase) && fieldValue == null ||
                            s.Equals(WiFieldReference.ClosedBy, StringComparison.InvariantCultureIgnoreCase) && fieldValue == null ||
                            s.Equals(WiFieldReference.Tags, StringComparison.InvariantCultureIgnoreCase) && fieldValue == null:

                            wi.Fields[fieldRef] = fieldValue;
                            break;
                        case var s when s.Equals(WiFieldReference.ChangedDate, StringComparison.InvariantCultureIgnoreCase):
                            break;
                        default:
                            if (fieldValue != null)
                            {
                                wi.Fields[fieldRef] = fieldValue;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "Failed to update fields.");
                    success = false;
                }
            }

            return success;
        }

        private bool ApplyAndSaveLinks(WiRevision rev, WorkItem wi)
        {
            var success = true;

            foreach (var link in rev.Links)
            {
                try
                {
                    var sourceWiId = _context.Journal.GetMigratedId(link.SourceOriginId);
                    var targetWiId = _context.Journal.GetMigratedId(link.TargetOriginId);

                    link.SourceWiId = sourceWiId;
                    link.TargetWiId = targetWiId;

                    if (link.TargetWiId == -1)
                    {
                        var errorLevel = Settings.IgnoreFailedLinks ? LogLevel.Warning : LogLevel.Error;
                        Logger.Log(errorLevel, $"'{link}' - target work item for Jira '{link.TargetOriginId}' is not yet created in Azure DevOps/TFS.");
                        success = false;
                        continue;
                    }

                    if (link.Change == ReferenceChangeType.Added && !_witClientUtils.AddAndSaveLink(link, wi))
                    {
                        success = false;
                    }
                    else if (link.Change == ReferenceChangeType.Removed && !_witClientUtils.RemoveAndSaveLink(link, wi))
                    {
                        success = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Failed to apply links for '{wi.Id}'.");
                    success = false;
                }
            }

            if (rev.Links.Any(l => l.Change == ReferenceChangeType.Removed))
                wi.Fields[WiFieldReference.History] = $"Removed link(s): { string.Join(";", rev.Links.Where(l => l.Change == ReferenceChangeType.Removed).Select(l => l.ToString()))}";
            else if (rev.Links.Any(l => l.Change == ReferenceChangeType.Added))
                wi.Fields[WiFieldReference.History] = $"Added link(s): { string.Join(";", rev.Links.Where(l => l.Change == ReferenceChangeType.Added).Select(l => l.ToString()))}";

            return success;
        }

        #endregion

        #region Import Iteration

        private Dictionary<string, object> GetIterationAttributes(string fullName)
        {
            var attributes = new Dictionary<string, object>();

            try
            {
                var iteration = _context.GetIteration(fullName);

                if (iteration.StartDate.HasValue)
                {
                    attributes.Add("startDate", iteration.StartDate.Value);
                }

                if (iteration.EndDate.HasValue)
                {
                    attributes.Add("finishDate", iteration.EndDate.Value);
                }
            }
            catch (Exception e)
            {
                Logger.Log(e, $"No exported configuration found for iteration: {fullName}", LogLevel.Debug);
            }

            return attributes;
        }

        #endregion
    }
}