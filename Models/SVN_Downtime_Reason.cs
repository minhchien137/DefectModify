using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DefectModify.Models
{
   [Table("SVN_Downtime_Reason")]
    public class SVN_Downtime_Reason
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string Reason_Code { get; set; }
        public string Reason_Name { get; set; }
    }

}