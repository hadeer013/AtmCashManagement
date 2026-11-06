using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atm.DataAccess.Persistence;

/// <summary>Used only by the dotnet-ef tooling to create migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AtmDbContext>
{
    public AtmDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__AtmDb")
                               ?? "Server=(localdb)\\MSSQLLocalDB;Database=AtmCashManagement;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AtmDbContext>().UseSqlServer(connectionString).Options;
        return new AtmDbContext(options);
    }
}
