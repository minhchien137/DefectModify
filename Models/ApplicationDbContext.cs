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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SVN_Defect_Record>()
                .HasKey(r => new { r.Item_code, r.Defect_Code, r.INSDatetime, r.Operation });
        }
    }
}