using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>
/// Builds an <see cref="AppDbContext"/> backed by a real Sqlite database that lives
/// only for the lifetime of one open connection — the same engine as production, so
/// unique indexes, query filters, and enum-to-string conversions all behave the same
/// way they do against smartschool.db, without touching disk.
/// </summary>
public sealed class TestDbContextFactory : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Context { get; }

    public TestDbContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
    }

    /// <summary>Another context on the same in-memory database (as a second connection would be in production).</summary>
    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
