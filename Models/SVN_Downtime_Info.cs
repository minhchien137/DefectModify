using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DefectModify.Models
{
    [Table("SVN_Downtime_Info")]
    public class SVN_Downtime_Info
    {

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public string? Code { get; set; }

        public string? Name { get; set; }

        public string? State { get; set; }

        public string? Operation { get; set; }

        public string? EstimateTime { get; set; }

        public string? Description { get; set; }

        public string? Image { get; set; }

        public DateTime? Datetime { get; set; }

        [Column("ISS-Code")]
        public string? ISS_Code { get; set; }

        [Column("SVNCode")]
        public string? SVNCode { get; set; }

    }

}