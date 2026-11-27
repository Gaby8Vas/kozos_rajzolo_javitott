using KozosRajzolo.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KozosRajzolo.Data;

public sealed class DrawingDbContext(DbContextOptions<DrawingDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<DrawingStroke> Strokes => Set<DrawingStroke>();
    public DbSet<DrawingRoom> DrawingRooms => Set<DrawingRoom>();
    public DbSet<RoomParticipant> RoomParticipants => Set<RoomParticipant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var stroke = modelBuilder.Entity<DrawingStroke>();
        stroke.HasKey(x => x.Id);
        stroke.Property(x => x.RoomCode).HasMaxLength(32).IsRequired();
        stroke.Property(x => x.UserName).HasMaxLength(60).IsRequired();
        stroke.Property(x => x.Color).HasMaxLength(20).IsRequired();
        stroke.Property(x => x.PointsJson).HasColumnType("longtext").IsRequired();
        stroke.HasIndex(x => new { x.RoomCode, x.Id });
        stroke.HasOne<DrawingRoom>().WithMany().HasForeignKey(x => x.RoomCode).OnDelete(DeleteBehavior.Cascade);

        var room = modelBuilder.Entity<DrawingRoom>();
        room.HasKey(x => x.RoomCode);
        room.Property(x => x.RoomCode).HasMaxLength(32);
        room.Property(x => x.Name).HasMaxLength(100).IsRequired();
        room.Property(x => x.OwnerUserId).HasMaxLength(450).IsRequired();
        room.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);

        var participant = modelBuilder.Entity<RoomParticipant>();
        participant.HasKey(x => x.Id);
        participant.Property(x => x.RoomCode).HasMaxLength(32).IsRequired();
        participant.Property(x => x.UserId).HasMaxLength(450).IsRequired();
        participant.Property(x => x.Role).HasMaxLength(20).IsRequired();
        participant.HasIndex(x => new { x.RoomCode, x.UserId }).IsUnique();
        participant.HasOne<DrawingRoom>().WithMany().HasForeignKey(x => x.RoomCode).OnDelete(DeleteBehavior.Cascade);
        participant.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
