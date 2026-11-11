# GMO.OpenTelemetry.Npgsql

Npgsql (PostgreSQL) instrumentation for `GMO.OpenTelemetry`. Targets net8.0, net9.0, and net10.0 only:
Npgsql 10 ships no .NET Framework build.

Npgsql instruments itself (ActivitySource and Meter both named `Npgsql`), so this package has two halves:

- **Subscription** on the OpenTelemetry providers: `AddNpgsqlInstrumentationWithEnrichment` (traces) and
  `AddNpgsqlMetrics` (metrics).
- **Enrichment** on the data source: `ConfigureTracingWithEnrichment` on `NpgsqlDataSourceBuilder` or
  `NpgsqlSlimDataSourceBuilder` (or `UseEnrichment` inside your own `ConfigureTracing` callback). With
  `enableTextEnrichment: true` each command span gets `db.commandTimeout`, `db.commandType`,
  `db.transaction.isolation_level` and `db.params` (JSON), the same tags as
  `GMO.OpenTelemetry.SqlClient`. Batch spans (EF Core `SaveChanges`) get `db.commandTimeout`,
  `db.operation.batch.size`, `db.transaction.isolation_level` and `db.params` as a JSON array with one
  entry per batch command. `commandFilter` drops spans for commands it returns `false` for.

`db.params` takes its shape from the parameter names:

| Parameters | `db.params` |
|---|---|
| All named (`@id`, `@name`) | Object keyed by name: `{"id":42,"name":"smoke"}` |
| All unnamed (positional `$1`, `$2`) | Array in order: `[42,"smoke"]`; a null value stays as `null` to keep positions |
| Mixed | Object; each unnamed parameter is keyed by its placeholder `"$N"` (1-based position among all parameters): `{"id":42,"$2":"smoke"}` |

Null values are otherwise left out, and no tag is set when every value is null. In a batch, a command
without parameters is `{}` and a positional command is a nested array: `[{"id":7},[42,"smoke"],{}]`.

Npgsql always records `db.query.text`, adds an `exception` event and sets error status on failure, so
there is no `recordException` switch. Enrichment only applies to connections from a data source built
with it.

## Usage

```csharp
services.AddGmoOpenTelemetry(options.Telemetry,
    additionalTracing: tracing => tracing.AddNpgsqlInstrumentationWithEnrichment(),
    additionalMetrics: metrics => metrics.AddNpgsqlMetrics());

var dataSource = new NpgsqlDataSourceBuilder(connectionString)
    .ConfigureTracingWithEnrichment(enableTextEnrichment: options.Telemetry.EnableSqlInstrumentationText)
    .Build();
```

With EF Core, configure the data source through the provider options:

```csharp
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString,
    npgsql => npgsql.ConfigureDataSource(ds => ds.ConfigureTracingWithEnrichment(enableTextEnrichment: true))));
```

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](../../LICENSE).
