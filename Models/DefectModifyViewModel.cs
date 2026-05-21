namespace DefectModify.Models
{
    public class DefectModifyViewModel
    {
        public List<SVN_Defect_Record_History> HistoryRecords { get; set; } = new();
        public List<SVN_Defect_Record> DefectRecords { get; set; } = new();
    }
}