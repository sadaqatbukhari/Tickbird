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

TimescaleDB, CrateDB and QuestDB all accept PostgreSQL connections, so they share
`PostgresClientBase` (`Tickbird.Core/Data/Sql`), which already implements listing databases and tables, columns as tags and
fields, tag values, queries (with cancel), writes, users and roles, privileges, running queries, diagnostics and statistics
with PostgreSQL's catalog queries. `TimescaleDbClient` and `CrateDbClient` are the examples: they override only what is special.
`TimescaleDbClient` adds to the PostgreSQL catalog; `CrateDbClient` replaces the catalog queries with `information_schema` and `sys`.

1. Add a value to `DatabaseKind`.
2. Write `YourClient : PostgresClientBase`.
   - Override the `virtual` members where the generic catalog queries do not fit (`HiddenSchemas`, `DefaultSchema`,
     `MeasurementNamesSql`, `ColumnsQuery`, `SchemaSql`, `IsTagType`, `ClearTableStatement`, `GetRelationKeywordAsync`,
     `ExecuteBatchAsync`, `GetVersionTextAsync`, `StatisticQueries`, `GetSeriesNamesAsync`...), and the operations the base
     reports as not supported, if your database has them.
   - Keep the SQL in `public const` strings or `public static` builders so a test can check it.
3. Add a `static readonly DatabaseProfile Profile` to the client (see `TimescaleDbClient.Profile`) and register it in the static
   constructor of `DatabaseProfiles`.
4. Write tests with `FakeSqlRunner` (see `TimescaleDbClientTests` and `CrateDbClientTests`): they record the SQL and return canned rows, so no server is needed.
5. Add the kind to `PostgresFamilyContractTests.Families`. It runs the same checks against every database of the family: what the profile
   supports must work, what it does not support must say so without reaching the server, errors become failed results.

## Any other database

Derive from `DatabaseClient` directly and fill in the abstract members. Return query results as `ResultSeries` (columns and rows;
timestamps as `DateTime` in UTC). Choose capabilities honestly: a command whose capability is missing is hidden, so you can
start with a read-only client (list databases and tables, run a query) and add features one at a time.

## Design patterns that make this reusable

| Pattern | Where | What it saves |
| --- | --- | --- |
| **Template method** | `PostgresClientBase` | The flow of every operation is written once; a database overrides the `virtual` step that differs (a catalog query, a statement, the default schema). |
| **Strategy** | `DatabaseProfile` (capabilities, terms, `SampleQuery`, `ValidateConnection`) | The UI asks the profile instead of testing which database it has, so no view contains `if (database == ...)`. |
| **Registry / factory** | `DatabaseProfiles`, `DatabaseClientFactory` | A database is added by registering one profile; the connection dialog, the commands and the client creation follow. |
| **Adapter** | `ISqlRunner` / `NpgsqlRunner` | The clients write SQL and read rows; only the runner talks to the driver, so tests replace it with a fake. |
| **Contract tests** | `PostgresFamilyContractTests` | One set of checks for every database of the family. |

## What the connection dialog and the toolbar take from the profile

| Profile member | Effect |
| --- | --- |
| `UsesDatabaseName = false` | The dialog leaves out the Database field (CrateDB has one database). |
| `DefaultUsername` | A new connection starts with this user (`crate`); a user the person typed is kept when they change database. |
| `ConnectionHint` | A note under the server fields, for example which port speaks which protocol. |
| `ValidateConnection` | Extra checks when saving, testing or pinging (CrateDB refuses its HTTP port). |
| `UrlSchemes` | The schemes a pasted connection string may use (`crate`, `postgresql`...). When several databases claim one (`http`), the default port decides. `ConnectionUrl` does the parsing. |
| `SignInHint` | What to try when signing in fails, shown with the diagnosis of **Test connection**. |
| `Capabilities` | A toolbar button whose capability is missing is hidden, and so is a separator left with no buttons beside it (`MainWindow.ToolbarCapabilities`). `AdministratorRights` also decides whether the user dialogs offer an Administrator option. |
| `Terms` | Tooltips and menus: Create/Drop **Database**, Show **Text Columns**, **Partitions**, Delete All Rows... |

## What helps a person fill in the dialog

| Piece | Where | What it does |
| --- | --- | --- |
| `ConnectionUrl` | `Tickbird.Core/Providers` | Reads a pasted connection string into a kind, host, port, user, password or token, database and SSL. |
| `ConnectionAdvisor` | same | Notes while typing (errors, warnings, information), a suggested name, and `CleanHost` / `PortInHost` for the host box. Database-specific rules come from the profile. |
| `ConnectionDiagnostics`, `ConnectionHints` | same | Tests a connection in steps and explains a failure in plain words with a hint. The network and the client are passed in, so tests run it without a server. `IConnectionTester` lets the dialog tests replace it. |

A new database gets all of this by filling in `UrlSchemes` and `SignInHint` on its profile.

## Words and shapes the UI adapts to

- `DatabaseTerms` renames menu items and tooltips (for TimescaleDB, "Tag Keys" becomes "Text Columns" and "Measurement" becomes "Table").
- `RetentionPerTable` and `ContinuousQueryWritesToDestination` choose the shape of the retention and continuous query dialogs.
- `ServerDiagnostics.Properties` and `ServerStats.Custom` let a database report its own facts and statistics in the shared tabs.

## Testing against a real server

`TimescaleDbIntegrationTests` and `CrateDbIntegrationTests` show how: it does nothing unless an environment variable names a server, then it creates its
own database and exercises every command. Copy it for a new database.
