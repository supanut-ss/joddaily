using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LineJod.Tests;

/// <summary>
/// The tests run on SQLite built from the EF model, so nothing else would notice db/schema.sql and the model drifting apart
/// (a missing column only shows up as an error on the real MariaDB). This compares tables, columns, nullability and column types.
/// </summary>
public class SchemaTests : DbTestBase
{
    private record Col(string Type, bool Nullable);

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LineJod.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static Dictionary<string, Dictionary<string, Col>> ParseSchema(string sql)
    {
        var tables = new Dictionary<string, Dictionary<string, Col>>();
        foreach (Match t in Regex.Matches(sql, @"CREATE TABLE (?<name>\w+) \((?<body>.*?)\)\s*ENGINE", RegexOptions.Singleline))
        {
            var cols = new Dictionary<string, Col>();
            var primaryKey = new HashSet<string>();
            foreach (var raw in t.Groups["body"].Value.Split('\n'))
            {
                var line = Regex.Replace(raw, @"--.*$", "").Trim().TrimEnd(',');
                if (line.Length == 0) continue;
                var pk = Regex.Match(line, @"^PRIMARY KEY \((?<cols>[^)]*)\)");
                if (pk.Success) { foreach (var c in pk.Groups["cols"].Value.Split(',')) primaryKey.Add(c.Trim()); continue; }
                if (Regex.IsMatch(line, @"^(KEY|UNIQUE|CONSTRAINT)\b")) continue;

                var m = Regex.Match(line, @"^(?<col>\w+)\s+(?<type>\w+(\(\d+\))?)(?<rest>.*)$");
                Assert.True(m.Success, "cannot read column line: " + line);
                var rest = m.Groups["rest"].Value;
                var nullable = !(rest.Contains("NOT NULL") || rest.Contains("PRIMARY KEY"));
                cols[m.Groups["col"].Value] = new Col(m.Groups["type"].Value.ToUpperInvariant(), nullable);
            }
            foreach (var c in primaryKey) cols[c] = cols[c] with { Nullable = false };
            tables[t.Groups["name"].Value] = cols;
        }
        return tables;
    }

    private static bool TypeMatches(Type clr, string sqlType) => (Nullable.GetUnderlyingType(clr) ?? clr) switch
    {
        var t when t == typeof(string) => sqlType.StartsWith("VARCHAR") || sqlType.StartsWith("CHAR") || sqlType == "TEXT",
        var t when t == typeof(long) => sqlType == "BIGINT",
        var t when t == typeof(int) => sqlType == "INT",
        var t when t == typeof(bool) => sqlType == "TINYINT(1)",
        var t when t == typeof(DateOnly) => sqlType == "DATE",
        var t when t == typeof(DateTime) => sqlType.StartsWith("DATETIME"),
        _ => false
    };

    [Fact]
    public void Schema_sql_has_every_table_and_column_of_the_model_with_matching_nullability_and_type()
    {
        var schema = ParseSchema(File.ReadAllText(RepoFile("db/schema.sql")));
        var problems = new List<string>();

        var modelTables = new HashSet<string>();
        foreach (var entity in Db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            modelTables.Add(table);
            if (!schema.TryGetValue(table, out var cols)) { problems.Add($"table {table} is in the model but not in schema.sql"); continue; }

            var modelCols = new HashSet<string>();
            foreach (var p in entity.GetProperties())
            {
                var name = p.GetColumnName(StoreObjectIdentifier.Table(table))!;
                modelCols.Add(name);
                if (!cols.TryGetValue(name, out var col)) { problems.Add($"{table}.{name} is in the model but not in schema.sql"); continue; }
                if (p.IsNullable != col.Nullable) problems.Add($"{table}.{name}: model nullable={p.IsNullable}, schema nullable={col.Nullable}");
                if (!TypeMatches(p.ClrType, col.Type)) problems.Add($"{table}.{name}: {p.ClrType.Name} does not fit SQL type {col.Type}");
            }
            foreach (var name in cols.Keys.Where(n => !modelCols.Contains(n))) problems.Add($"{table}.{name} is in schema.sql but not in the model");
        }
        foreach (var table in schema.Keys.Where(t => !modelTables.Contains(t))) problems.Add($"table {table} is in schema.sql but not in the model");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_migration_creates_only_tables_and_columns_that_schema_sql_has()
    {
        var schema = ParseSchema(File.ReadAllText(RepoFile("db/schema.sql")));
        foreach (var file in Directory.GetFiles(RepoFile("db/migrations"), "*.sql"))
        {
            var sql = File.ReadAllText(file);
            foreach (var (table, cols) in ParseSchema(sql))
            {
                Assert.True(schema.ContainsKey(table), $"{Path.GetFileName(file)}: table {table} is not in schema.sql");
                Assert.Equal(schema[table].Keys.OrderBy(x => x), cols.Keys.OrderBy(x => x));
            }
            foreach (Match add in Regex.Matches(sql, @"ALTER TABLE (?<t>\w+)(?<body>.*?);", RegexOptions.Singleline))
                foreach (Match c in Regex.Matches(add.Groups["body"].Value, @"ADD COLUMN (?<c>\w+)"))
                    Assert.True(schema[add.Groups["t"].Value].ContainsKey(c.Groups["c"].Value), $"{Path.GetFileName(file)}: {add.Groups["t"].Value}.{c.Groups["c"].Value} is not in schema.sql");
        }
    }
}
