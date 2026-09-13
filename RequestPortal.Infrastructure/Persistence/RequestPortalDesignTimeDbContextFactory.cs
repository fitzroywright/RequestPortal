using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class RequestPortalDesignTimeDbContextFactory : IDesignTimeDbContextFactory<RequestPortalDbContext>
{
    public RequestPortalDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("REQUESTPORTAL_MIGRATION_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=requestportal_migrations;Username=postgres;Password=postgres";

        DbContextOptionsBuilder<RequestPortalDbContext> options = new();
        options.UseNpgsql(connectionString);
        return new RequestPortalDbContext(options.Options);
    }
}
