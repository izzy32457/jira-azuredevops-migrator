
namespace JiraExport
{
    public class JiraSettings
    {
        public string UserID { get; }
        public string Pass { get; }
        public string Url { get; }
        public string Project { get; set; }
        public string EpicLinkField { get; set; }
        public string SprintField { get; set; }
        public string UserMappingFile { get; set; }
        public int BatchSize { get; set; }
        public string AttachmentsDir { get; set; }
        public string SprintsDir { get; set; }
        public string JQL { get; set; }
        public bool UsingJiraCloud { get; set; }
        public string BoardID { get; set; }

        public JiraSettings(string userID, string pass, string url, string project)
        {
            UserID = userID;
            Pass = pass;
            Url = url;
            Project = project;
        }
    }
}