using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DefectModify.Models
{
    [Table("DefectEditLog")]
    public class DefectEditLog
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Tên bảng bị ảnh hưởng</summary>
        public string? TableName { get; set; }

        /// <summary>Loại thao tác: Edit | Delete</summary>
        public string? Action { get; set; }

        /// <summary>Chuỗi định danh bản ghi bị thay đổi</summary>
        public string? RecordId { get; set; }

        /// <summary>Giá trị cũ (JSON)</summary>
        public string? OldValues { get; set; }

        /// <summary>Giá trị mới (JSON) — null nếu là Delete</summary>
        public string? NewValues { get; set; }

        public DateTime ModifiedAt { get; set; }
        public string? ModifiedBy { get; set; }
    }
}