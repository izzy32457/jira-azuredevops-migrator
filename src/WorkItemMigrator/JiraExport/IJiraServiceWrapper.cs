using Atlassian.Jira;
using Atlassian.Jira.Remote;
using System.Collections.Generic;

namespace JiraExport
{
    public interface IJiraServiceWrapper
    {
        IIssueFieldService Fields { get; }
        IIssueService Issues { get; }
        IIssueLinkService Links { get; }
        IJiraRestClient RestClient { get; }
        IJiraUserService Users { get; }

        IEnumerable<JiraSprint> GetSprints(string boardId);
    }
}
