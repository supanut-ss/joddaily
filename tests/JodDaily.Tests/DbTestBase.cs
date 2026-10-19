using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using JodDaily.Data;

namespace JodDaily.Tests;

/// <summary>SQLite in-memory database built from the EF model (checks relationships and the business logic, not SQL Server specifics).</summary>
public abstract class DbTestBase : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    protected AppDbContext Db { get; }

    protected DbTestBase()
    {
        _conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).UseSnakeCaseNamingConvention().Options);
        Db.Database.EnsureCreated();
    }

    protected AppUser AddUser(string name)
    {
        var u = new AppUser { LineUserId = "U" + Guid.NewGuid().ToString("N"), DisplayName = name, IsFriend = true, CreatedAt = DateTime.UtcNow };
        Db.Users.Add(u);
        Db.SaveChanges();
        return u;
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }
}
