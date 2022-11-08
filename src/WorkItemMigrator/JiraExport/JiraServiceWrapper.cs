using Atlassian.Jira;
using Atlassian.Jira.Remote;
using Migration.Common.Log;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using Migration.Common;

namespace JiraExport
{
    public class JiraServiceWrapper : IJiraServiceWrapper
    {
        private readonly Jira _jira;

        public IIssueFieldService Fields => _jira.Fields;
        public IIssueService Issues => _jira.Issues;
        public IIssueLinkService Links => _jira.Links;
        public IJiraRestClient RestClient => _jira.RestClient;
        public IJiraUserService Users => _jira.Users;

        public JiraServiceWrapper(JiraSettings jiraSettings)
        {
            try
            {
                Logger.Log(LogLevel.Info, "Connecting to Jira...");

                _jira = Jira.CreateRestClient(jiraSettings.Url, jiraSettings.UserID, jiraSettings.Pass);
                _jira.RestClient.RestSharpClient.AddDefaultHeader("X-Atlassian-Token", "no-check");
                if (jiraSettings.UsingJiraCloud)
                    _jira.RestClient.Settings.EnableUserPrivacyMode = true;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Could not connect to Jira!", LogLevel.Critical);
            }
        }

        public IEnumerable<JiraSprint> GetSprints(string boardId)
        {
            var resource = $"/rest/agile/1.0/board/{boardId}/sprint";
            var startIndex = 0;
            bool isLast;
            do
            {
                JObject board;
                try
                {
                    board = (JObject)_jira.RestClient.ExecuteRequestAsync(Method.GET, $"{resource}?startAt={startIndex}").Result;
                }
                catch (Exception e)
                {
                    Logger.Log(e, $"Failed to download sprints for board with Id: {boardId}");
                    yield break;
                }

                var sprints = board.SelectTokens("$.values.[*]", false).Cast<JObject>().ToList();
                foreach (var sprint in sprints)
                {
                    yield return new JiraSprint
                    {
                        OriginId = sprint.ExValue<string>("$.id"),
                        OriginBoardId = sprint.ExValue<string>("$.originBoardId"),
                        Name = sprint.ExValue<string>("$.name"),
                        State = sprint.ExValue<string>("$.state"),
                        Goal = sprint.ExValue<string>("$.goal"),
                        StartDate = sprint.ExValue<DateTime?>("$.startDate"),
                        EndDate = sprint.ExValue<DateTime?>("$.endDate"),
                        ActivatedDate = sprint.ExValue<DateTime?>("$.activatedDate"),
                        CompletedDate = sprint.ExValue<DateTime?>("$.completeDate"),
                    };
                    startIndex++;
                }
                
                isLast = board.ExValue<bool>("$.isLast");
            } while (!isLast);
        }
    }
}
