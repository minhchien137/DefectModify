using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DefectModify.Models
{

    [Table("SVN_Defect_Record_History")]
    public class SVN_Defect_Record_History
    {

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string? Work_order { get; set; }
        public string? Item_code { get; set; }

        public string? Defect_Code { get; set; }

        public string? Defect_Name { get; set; }

        public int? Qty_NG { get; set; }

        public string? INSDatetime { get; set; }

        public string? Operation { get; set; }


        public string? Employer_code { get; set; }

        public string? Employer_name { get; set; }

        public string? Note { get; set; }

        public string? Image_error { get; set; }

        public DateTime? Time_line { get; set; }

    }
}



