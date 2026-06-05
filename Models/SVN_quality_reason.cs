
using System.ComponentModel.DataAnnotations.Schema;

namespace DefectModify.Models
{
    [Table("SVN_quality_reason")]
    public class SVN_quality_reason
    {
        public int id { get; set; }

        public string create_uid { get; set; }

        public string write_uid { get; set; }

        public string name { get; set; }

        public DateTime create_date { get; set; }

        public DateTime write_date { get; set; }

        public string code { get; set; }

        public int priority { get; set; }

        public string operation { get; set; }
    }
}
