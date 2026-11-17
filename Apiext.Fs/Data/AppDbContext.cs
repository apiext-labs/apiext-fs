using Microsoft.EntityFrameworkCore;
using Apiext.Fs.Models.Entities;

namespace Apiext.Fs.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserAcl> UserAcls => Set<UserAcl>();
    public DbSet<BucketPolicy> BucketPolicies => Set<BucketPolicy>();
    public DbSet<BackgroundJobRun> BackgroundJobRuns => Set<BackgroundJobRun>();
    public DbSet<ObjectIndexEntry> ObjectIndex => Set<ObjectIndexEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Username)
             .IsUnique();
        });

        modelBuilder.Entity<UserAcl>(e =>
        {
            e.HasOne(a => a.User)
             .WithMany(u => u.Acls)
             .HasForeignKey(a => a.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(a => new { a.UserId, a.BucketName })
             .IsUnique();
        });

        modelBuilder.Entity<BucketPolicy>(e =>
        {
            e.HasKey(p => p.BucketName);
        });

        modelBuilder.Entity<BackgroundJobRun>(e =>
        {
            e.HasIndex(j => new { j.JobType, j.Status });
            e.HasIndex(j => j.CreatedUtc);
        });

        // Composite primary key doubles as the lookup index for ListObjects /
        // GetObject metadata reads.
        modelBuilder.Entity<ObjectIndexEntry>(e =>
        {
            e.HasKey(x => new { x.Bucket, x.Key });
        });
    }
}
