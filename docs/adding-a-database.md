# Adding a database to Tickbird

Tickbird is built so that a new database needs **one client class and one profile**. The connection tree, the menus and
toolbar, the query editor with IntelliSense, the results (grid, chart, map), the filter, export, query history and the
connection dialog are shared and need no change.

## The pieces

| Piece | Where | What it is |
| --- | --- | --- |
| `DatabaseKind` | `Tickbird.Core/Providers/DatabaseProfile.cs` | One value per database (saved in each connection). |
| `DatabaseProfile` | same file | What the app needs to know: name, default port, SQL or InfluxQL, sign-in style (user and password, or token), **capabilities**, the **words** the UI uses (table, columns, roles...), a starter query and extra completion items. |
| `DatabaseCapabilities` | same file | Flags such as `Users`, `RetentionPolicies`, `ContinuousQueries`, `Series`. A command is shown and enabled only when the profile says the database can do it. |
| `DatabaseClient` | `Tickbird.Core/Data/DatabaseClient.cs` | The operations every view calls: list databases and tables, tag and field columns, run a query, users, privileges, retention, continuous queries, running queries, diagnostics, statistics. Operations the database cannot do throw `NotSupportedException` (and the profile hides their commands). |
| `DatabaseProfiles` | `Tickbird.Core/Providers/DatabaseProfiles.cs` | The registry. The connection dialog lists whatever is registered. |

## A database that speaks the PostgreSQL protocol

CrateDB, QuestDB and TimescaleDB all accept PostgreSQL connections, so they share
`PostgresClientBase` (`Tickbird.Core/Data/Sql`), which already implements listing databases and tables, columns as tags and
fields, tag values, queries (with cancel), writes, users and roles, privileges, running queries, diagnostics and statistics
with PostgreSQL's catalog queries. `TimescaleDbClient` is the example: it overrides only what is special.

1. Add a value to `DatabaseKind`.
2. Write `YourClient : PostgresClientBase`.
   - Override the `virtual` members where the generic catalog queries do not fit (`HiddenSchemas`, `GetVersionTextAsync`,
     `StatisticQueries`, `GetSeriesNamesAsync`...), and the operations the base reports as not supported, if your database has them.
   - Keep the SQL in `public const` strings or `public static` builders so a test can check it.
3. Add a `static readonly DatabaseProfile Profile` to the client (see `TimescaleDbClient.Profile`) and register it in the static
   constructor of `DatabaseProfiles`.
4. Write tests with `FakeSqlRunner` (see `TimescaleDbClientTests`): they record the SQL and return canned rows, so no server is needed.

## Any other database

Derive from `DatabaseClient` directly and fill in the abstract members. Return query results as `ResultSeries` (columns and rows;
timestamps as `DateTime` in UTC). Choose capabilities honestly: a command whose capability is missing is hidden, so you can
start with a read-only client (list databases and tables, run a query) and add features one at a time.

## Words and shapes the UI adapts to

- `DatabaseTerms` renames menu items and tooltips (for TimescaleDB, "Tag Keys" becomes "Text Columns" and "Measurement" becomes "Table").
- `RetentionPerTable` and `ContinuousQueryWritesToDestination` choose the shape of the retention and continuous query dialogs.
- `ServerDiagnostics.Properties` and `ServerStats.Custom` let a database report its own facts and statistics in the shared tabs.

## Testing against a real server

`TimescaleDbIntegrationTests` shows how: it does nothing unless an environment variable names a server, then it creates its
own database and exercises every command. Copy it for a new database.
