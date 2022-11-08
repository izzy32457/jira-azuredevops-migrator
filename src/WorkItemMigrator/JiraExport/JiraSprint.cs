using System;

namespace JiraExport
{
    public class JiraSprint
    {
        public string OriginId { get; set; }

        public string OriginBoardId { get; set; }

        public string Name { get; set; }

        public string State { get; set; }

        public string Goal { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public DateTime? ActivatedDate { get; set; }

        public DateTime? CompletedDate { get; set; }
    }
}
