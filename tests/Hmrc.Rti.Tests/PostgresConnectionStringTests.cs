using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hmrc.Rti.Tests;

public class PostgresConnectionStringTests
{
    private const string Explicit =
        "Host=db.internal;Port=5432;Database=payroll;Username=svc;Password=secret";

    private const string DatabaseUrl = "postgresql+asyncpg://dev:pw@localhost:55444/payroll";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void ConnectionStringAloneIsUsedVerbatim()
    {
        // How Azure configures the service: ConnectionStrings__Postgres only.
        var resolved = PostgresConnectionString.Resolve(Config(("ConnectionStrings:Postgres", Explicit)));

        Assert.Equal(Explicit, resolved);
    }

    [Fact]
    public void ConnectionStringWinsOverDatabaseUrl()
    {
        var resolved = PostgresConnectionString.Resolve(Config(
            ("ConnectionStrings:Postgres", Explicit),
            ("DATABASE_URL", DatabaseUrl)));

        Assert.Equal(Explicit, resolved);
    }

    [Fact]
    public void DatabaseUrlIsUsedWhenNoConnectionStringIsSet()
    {
        var resolved = PostgresConnectionString.Resolve(Config(("DATABASE_URL", DatabaseUrl)));

        Assert.Equal("Host=localhost;Port=55444;Database=payroll;Username=dev;Password=pw", resolved);
    }

    [Fact]
    public void DatabaseUrlWinsOverTheShippedAppsettings()
    {
        // The regression: appsettings.json used to set ConnectionStrings:Postgres,
        // which shadowed an exported DATABASE_URL.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection([new KeyValuePair<string, string?>("DATABASE_URL", DatabaseUrl)])
            .Build();

        var resolved = PostgresConnectionString.Resolve(configuration);

        Assert.Equal("Host=localhost;Port=55444;Database=payroll;Username=dev;Password=pw", resolved);
    }

    [Fact]
    public void LocalDefaultIsUsedWhenNothingIsSet()
    {
        Assert.Equal(PostgresConnectionString.LocalDefault, PostgresConnectionString.Resolve(Config()));
        Assert.Equal(
            "Host=localhost;Port=5432;Database=payroll;Username=payroll;Password=payroll_dev_pwd",
            PostgresConnectionString.LocalDefault);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDatabaseUrlFallsBackToTheLocalDefault(string blank)
    {
        var resolved = PostgresConnectionString.Resolve(Config(("DATABASE_URL", blank)));

        Assert.Equal(PostgresConnectionString.LocalDefault, resolved);
    }

    [Fact]
    public void UrlInConnectionStringIsConverted()
    {
        var resolved = PostgresConnectionString.Resolve(Config(
            ("ConnectionStrings:Postgres", "postgres://svc:p%40ss@db.internal/payroll")));

        Assert.Equal("Host=db.internal;Port=5432;Database=payroll;Username=svc;Password=p@ss", resolved);
    }
}
