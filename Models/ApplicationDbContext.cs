using Microsoft.EntityFrameworkCore;

namespace DefectModify.Models
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<SVN_Defect_Record> SVN_Defect_Records { get; set; }
        public DbSet<SVN_Defect_Record_History> SVN_Defect_Record_Histories { get; set; }
        public DbSet<DefectEditLog> DefectEditLogs { get; set; }

        public DbSet<SVN_Downtime_Info> SVN_Downtime_Infos { get; set; }

        public DbSet<SVN_Downtime_Reason> SVN_Downtime_Reasons { get; set; }

        public DbSet<SVN_quality_reason> SVN_quality_reasons { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SVN_Defect_Record>()
                .HasKey(r => new { r.Item_code, r.Defect_Code, r.INSDatetime, r.Operation });

            // Đảm bảo EF map đúng tên bảng trong DB
            modelBuilder.Entity<DefectEditLog>()
                .ToTable("DefectEditLog");
        }
    }
}
