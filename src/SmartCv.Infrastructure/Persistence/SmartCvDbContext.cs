using Microsoft.EntityFrameworkCore;

namespace SmartCv.Infrastructure.Persistence;

public class SmartCvDbContext(
    DbContextOptions<SmartCvDbContext> options
) : DbContext(options)
{
    // Bước A2 sẽ thêm DbSet cho các bảng.
}