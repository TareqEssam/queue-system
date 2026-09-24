using Microsoft.EntityFrameworkCore;
using QueueSystem.Data.Entities;

namespace QueueSystem.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Registration> Registrations => Set<Registration>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<DeskState> DeskStates => Set<DeskState>();
    public DbSet<QueueLog> QueueLogs => Set<QueueLog>();
    public DbSet<ArchiveRecord> ArchiveRecords => Set<ArchiveRecord>();
    public DbSet<PushRegistration> PushRegistrations => Set<PushRegistration>();
    public DbSet<SystemState> SystemStates => Set<SystemState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Employees
        modelBuilder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(32).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Desk);
        });

        // Registrations
        modelBuilder.Entity<Registration>(e =>
        {
            e.HasIndex(x => x.ClientNumber);
            e.HasIndex(x => x.CommercialRegister);
            e.HasIndex(x => x.CompanyName);
            e.Property(x => x.CommercialRegister).HasMaxLength(50);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(50);
        });

        // Tickets
        modelBuilder.Entity<Ticket>(e =>
        {
            e.HasIndex(x => x.ClientNumber);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.ClientNumber, x.Status });
            e.HasIndex(x => x.RegistrationId);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.HasOne(x => x.Registration)
             .WithMany()
             .HasForeignKey(x => x.RegistrationId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // DeskState
        modelBuilder.Entity<DeskState>(e =>
        {
            e.HasIndex(x => x.Desk).IsUnique();
        });

        // QueueLog
        modelBuilder.Entity<QueueLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.ClientNumber);
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.Property(x => x.EmployeeDisplayName).HasMaxLength(100);
        });

        // Archive
        modelBuilder.Entity<ArchiveRecord>(e =>
        {
            e.HasIndex(x => x.CommercialRegister);
            e.HasIndex(x => x.CompanyName);
            e.HasIndex(x => x.ClientNumber);
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.CommercialRegister).HasMaxLength(50);
            e.Property(x => x.EmployeeDisplayName).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.StatusFinal).HasMaxLength(30);
        });

        // Push
        modelBuilder.Entity<PushRegistration>(e =>
        {
            e.HasIndex(x => new { x.TicketId, x.FcmToken }).IsUnique();
            e.Property(x => x.FcmToken).HasMaxLength(512).IsRequired();
            e.Property(x => x.PushType).HasMaxLength(20).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(2048);
            e.Property(x => x.P256dh).HasMaxLength(256);
            e.Property(x => x.Auth).HasMaxLength(256);
            e.Property(x => x.Language).HasMaxLength(5).IsRequired();
            e.HasOne(x => x.Ticket)
             .WithMany()
             .HasForeignKey(x => x.TicketId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // SystemState (صف واحد فقط)
        modelBuilder.Entity<SystemState>(e =>
        {
            e.HasData(new SystemState
            {
                Id = 1,
                IsOpen = true,
                QueueSequence = 0,
                LastCalledNumber = 0,
                ActiveDayKey = "",
                LiveEventSeq = 0,
                WaitingCount = 0,
                UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });
    }
}
