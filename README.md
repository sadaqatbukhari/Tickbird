<p align="center"><img src="src/Tickbird.Desktop/Assets/BrandLogo.png" alt="Tickbird" height="72"></p>

# Tickbird
> **Disclaimer.** The program is provided AS IS with NO WARRANTY OF ANY KIND, INCLUDING THE WARRANTY OF DESIGN, MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE. The provider of this tool is not responsible or liable for any loss of data, or any other loss or damage, arising from its use. Back up your data before running commands that change or delete it. See [Disclaimer](#disclaimer).

**Tickbird is a cross-platform desktop client for exploring and managing time series databases. It currently supports [InfluxDB](https://www.influxdata.com/time-series-platform/influxdb/) (1.x and 3.x), [TimescaleDB](https://www.timescale.com/) and [CrateDB](https://cratedb.com/), and is built so that more databases (such as QuestDB) can be added.**

Its inspiration comes from other similar database management tools such as [SQL Server Management Studio](https://en.wikipedia.org/wiki/SQL_Server_Management_Studio)
. Tickbird supports InfluxDB 1.x, InfluxDB 3.x, TimescaleDB and CrateDB.
The 1.x integration uses InfluxData's official `InfluxDB.Client` package, the 3.x
integration uses the official `InfluxDB3.Client` package, and TimescaleDB and CrateDB use the
[Npgsql](https://www.npgsql.org/) PostgreSQL driver.

## Current Release Features

* **InfluxDB 1.x and 3.x connections** - Choose the server generation when creating or editing a connection.
* **TimescaleDB** - Manage a TimescaleDB (PostgreSQL) server with the same tools as InfluxDB: databases, hypertables, tags and fields, chunks, retention policies, continuous aggregates, backfill, roles and privileges, running queries, diagnostics and statistics. See [Working with TimescaleDB](#working-with-timescaledb).
* **CrateDB** - Browse and manage a CrateDB cluster over its PostgreSQL protocol: tables in all schemas, text columns and other columns, partitions, users and privileges, running jobs, nodes and health. The toolbar shows only what CrateDB can do. See [Working with CrateDB](#working-with-cratedb).
* **Built for more databases** - Each database is described by a profile (what it can do, its words and defaults) and one client class, and the tree, menus, query editor, results, charts and maps are shared. See [adding a database](docs/adding-a-database.md).
* **Official InfluxData clients** - Both integrations use the standard packages maintained by InfluxData.
* **InfluxDB 3 SQL support** - Browse databases and tables, run SQL queries, write data, and perform health checks.
* **Optional InfluxDB 1.x authentication** - Anonymous servers can be tested and used without entering a username or password.
* **SQL Server-style query execution** - If text is selected, only the selected query text runs; otherwise, the entire editor runs.
* **Open and save query files** - Use **Query → Open Query** (`Ctrl+O`) and **Query → Save Query** (`Ctrl+S`).
* **Multi-line comments** - Toolbar buttons add or remove `--` comments across all selected lines.
* **Large-result stability** - Query results use virtual scrolling and create visible rows on demand instead of rendering the entire result set at once.
* **Millisecond timestamps** - The output window includes milliseconds in logged times.
* **Cross-platform build** - The Avalonia desktop app targets .NET 10 and runs on Windows, macOS, and Linux. The Windows installer includes the required runtime.
* **Upgrade-safe settings** - Installing a newer version updates the application while retaining saved connections and preferences.
* **GitHub update checks** - Startup checks for a newer published stable release and shows a clickable notice for 15 seconds. **Help → About** shows the update status, a release link when available, and a **Check again** button. Release tags use version numbers such as `v1.0.0`.
* **Fields by retention policy** - **Show Fields** includes a policy selector for InfluxDB 1.x. It initially selects the default policy when fields exist there, or the first policy containing fields. Changing the selection displays that policy's fields.
* **Avalonia desktop UI** - The query editors (AvaloniaEdit), result grid (Avalonia DataGrid), main window, toolbar, status bar, and context menus use open-source Avalonia controls.
* **InfluxQL IntelliSense** - Press `Ctrl+Space` for keywords and functions; connection-aware completion also includes measurements, fields, and tags.
* **Charts for query results** - Switch any result between **Grid** and **Chart**. Numeric columns are plotted over time with zoom, pan, a hover readout, a field picker, split by tag, and PNG export. See [Charting Query Results](#charting-query-results).
* **Stop, timeout and query history** - Stop a running query, set a query timeout, and reopen any earlier query from **View → Query History**.
* **Find and replace** - `Ctrl+F` / `Ctrl+H` in the query editor.
* **Object Explorer filter** - Find connections, databases and measurements as you type (`Ctrl+E`).
* **Filter results** - Filter rows by text or column conditions; charts and maps show the same rows. See [Filtering Query Results](#filtering-query-results).
* **Map for GPS results** - Switch a result to **Map** to draw its latitude/longitude columns as lines over satellite, hybrid or street maps. Bind the latitude, longitude and color columns, and pick the color of each value or range. See [Mapping Query Results](#mapping-query-results).
* **Resizable query workspace** - Drag the horizontal divider to allocate space between the query editor and results.
* **Resizable toolbar and status bar** - Compact, Visual Studio-style toolbar; when the window is too narrow, the buttons that do not fit move to the **⋯ More commands** menu. Long status messages are trimmed to fit. Show or hide either bar from the **View** menu.
* **Dockable Object Explorer** - Dock the connection tree left or right, auto-hide it to an edge tab, float it in its own window, or hide it (**View → Object Explorer**, `F8`, brings it back). See [Arranging the Object Explorer](#arranging-the-object-explorer).
* **Connection groups** - Organize servers into groups such as PROD and DEV. Groups appear as folders in the Object Explorer and as a filter in **Manage Connections**. See [Connection Groups](#connection-groups).
* **Type-the-name drop confirmation** - Dropping a database, measurement or series requires typing its exact name first. See [Confirming Destructive Actions](#confirming-destructive-actions).
* **Smarter IntelliSense** - The completion list stays open while you type, and after `tag = '` it offers that tag's actual values. See [Using the Query Editor](#using-the-query-editor).
* **SSMS-style editor colors** - Keywords, functions, strings, comments and operators use the familiar SQL Server Management Studio colors.
* **Refreshed UI** - A consistent blue theme with card-style panels, striped result grids, icon buttons in the connection dialogs, and a toolbar that adapts to the window width.

The following are planned features that are not yet implemented in the current version:

* _~~Retention Policy management~~ implemented, but not documented_
* _~~Custom stats view for server stats (diagnostics is presently supported)~~ implemented, but not documented_
* _~~Listing currently running queries~~ implemented, but not documented_ 
* _~~Stopping long running queries~~ implemented, but not documented_
* _An interface for writing point data directly to the database_

## Table of Contents

 - [Current Release Features](#current-release-features)
 - [Installation](#installation)
 - [Managing Connections](#managing-connections)
   - [Connection Settings](#connection-settings)
   - [Connection Groups](#connection-groups)
   - [Working with TimescaleDB](#working-with-timescaledb)
   - [Working with CrateDB](#working-with-cratedb)
 - [Connecting to a Server](#connecting-to-a-server)
 - [Working with Connections](#working-with-connections)
   - [Showing Server Diagnostics](#showing-server-diagnostics)
   - [Arranging the Object Explorer](#arranging-the-object-explorer)
 - [Working with Databases](#working-with-databases)
   - [Creating a Database](#creating-a-database)
   - [Dropping a Database](#dropping-a-database)
   - [Running a Database Query](#running-a-database-query)
   - [Using the Query Editor](#using-the-query-editor)
   - [Query History](#query-history)
   - [Working with Large Query Results](#working-with-large-query-results)
   - [Exporting Database Query Results](#exporting-database-query-results)
   - [Filtering Query Results](#filtering-query-results)
   - [Charting Query Results](#charting-query-results)
   - [Mapping Query Results](#mapping-query-results)
   - [Creating Continuous Queries](#creating-continuous-queries)
   - [Running a Backfill Query](#running-a-backfill-query)
   - [Listing Running Queries](#listing-running-queries)
   - [Stopping Long Running Queries](#stopping-long-running-queries)
 - [Working with Measurements and Series](#working-with-measurements-and-series)
   - [Running a Measurement Query](#running-a-measurement-query)
   - [Exporting Measurement Query Results](#exporting-measurement-query-results)
   - [Showing Tag Keys](#showing-tag-keys)
   - [Showing Tag Values](#showing-tag-values)
   - [Showing Field Keys](#showing-field-keys)
   - [Showing Series](#showing-series)
   - [Dropping Measurements](#dropping-measurements)
   - [Dropping Series](#dropping-series)
 - [Confirming Destructive Actions](#confirming-destructive-actions)
 - [Working with Users and Privileges](#working-with-users-and-privileges)
   - [Showing Users](#showing-users)
   - [Managing Users](#managing-users)
   - [Managing Privileges](#managing-privileges)
 - [Application Settings](#application-settings)
   - [Settings Overview](#settings-overview)
   - [Exporting Settings](#exporting-settings)
   - [Importing Settings](#importing-settings)
 - [Disclaimer](#disclaimer)
 - [License](#license)
   
## Installation

Download Tickbird for your system from the [releases page](https://github.com/sadaqatbukhari/Tickbird/releases). Every download includes the .NET runtime, so nothing else needs to be installed.

| Platform | Download | How to run |
| --- | --- | --- |
| Windows (x64) | `Tickbird-<version>-Setup-win-x64.exe` or the portable `…-win-x64.zip` | Run the installer, or unzip and start `Tickbird.Desktop.exe`. |
| Linux (x64) | `Tickbird-<version>-linux-x64.tar.gz` | Extract and run `./Tickbird/Tickbird.Desktop`. A `.desktop` launcher and icon are included. |
| macOS (Apple silicon) | `Tickbird-<version>-osx-arm64.zip` | Unzip and open `Tickbird.app`. |
| macOS (Intel) | `Tickbird-<version>-osx-x64.zip` | Unzip and open `Tickbird.app`. |

The builds are not code-signed. On Windows, SmartScreen may ask you to confirm; on macOS, right-click **Tickbird.app → Open** the first time (or run `xattr -cr Tickbird.app`).

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a tag such as `v1.0.0` is pushed. To build the downloads yourself, run `python installer/package.py <win-x64|linux-x64|osx-arm64|osx-x64>`; the files are written to `artifacts/release`.

You can build locally on Windows, macOS or Linux with the .NET 10 SDK:

```powershell
dotnet build Tickbird.slnx
```

Open the solution file `Tickbird.slnx` to get started. Run the app with:

```powershell
dotnet run --project src/Tickbird.Desktop
```

### Building the Windows installer

Build the self-contained 64-bit Windows MSI from the repository root:

```powershell
.\installer\build-installer.ps1
```

The installer builds the version in `Version.props` (currently **1.0.0**). Pass `-Bump` to raise the patch version first (`.\installer\build-installer.ps1 -Bump`). It creates an icon-branded
`Tickbird-<version>-Setup-win-x64.exe` and the underlying MSI in `artifacts\installer`.
The installer asks whether to create a Desktop shortcut and always creates a Start Menu shortcut.
It includes the .NET runtime, so the target computer does not need the .NET 10 runtime installed separately.

When Tickbird is already installed, the installer performs an in-place upgrade. Saved
connections and preferences are retained. Settings are stored per user at:

| Platform | Settings folder |
| --- | --- |
| Windows | `%APPDATA%\Tickbird\settings.json` |
| macOS | `~/Library/Application Support/Tickbird/settings.json` |
| Linux | `$XDG_CONFIG_HOME/tickbird/settings.json` (default `~/.config/tickbird/settings.json`) |

This location is independent of the installed application version, so later upgrades reuse the
same settings file. Uninstalling or upgrading the program does not remove this per-user file.

**Upgrading from an earlier version:** on first launch, Tickbird copies your settings, saved connections
and query history from the folder used by the previous version into the Tickbird folder. The old folder is
left untouched, and nothing is copied if Tickbird already has settings. Settings from the original WinForms
release (InfluxDB Studio 3.x) can be imported with **File → Import → From InfluxDB Studio**.

## Managing Connections

Upon running **Tickbird.Desktop.exe** you will be prompted with the **Manage Connections** dialog. This window will let you create, edit, and delete InfluxDB server connections.

![Empty Manage Connections Dialog](docs/img/ManageConnectionsDialog_Blank.png?raw=true "Empty Manage Connections Dialog")

Press the **Create** button to add your first InfluxDB connection using the **Connection Setting** dialog.

### Connection Settings

Use the Connection Settings dialog to configure the details of the InfluxDB connection:

 * **URL** - Optional. Paste a connection string such as `postgresql://app:pw@db.example.com:5433/metrics?sslmode=require`, `crate://crate@localhost:5434` or `https://influx.example.com:8181/telemetry?token=...` and the server, host, port, user, password or token, database and SSL are filled in. The box is cleared afterwards so a password does not stay on screen. `postgresql://` means TimescaleDB, `crate://` CrateDB; `http(s)://` means InfluxDB 1.x, or 3.x on port 8181.
 * **Name** - The name of the connection. This is the label you will see when working with this connection. While it is still the default it follows your choices, for example `crate@localhost`; a name you type is never replaced.
 * **Version** - Select the database: **InfluxDB 1.x**, **InfluxDB 3.x**, **TimescaleDB** or **CrateDB**. The port, the sign-in fields and the available commands follow your choice.
 * **Group** - Optional. Pick an existing group or type a new name, for example `PROD`. See [Connection Groups](#connection-groups).
 * **Address** - The InfluxDB server's host URI. Exclude protocol information. **Port** is filled in to the right.
 * **Database** - The database to use for the connection. Leave this blank to list all databases _(requires admin privileges)_.
 * **Username / Password** - Credentials for an InfluxDB 1.x connection.
 * **API Token** - Bearer token for an InfluxDB 3.x connection.
 * **Use SSL** - Whether or not to use SSL security (HTTPS) when connecting to InfluxDB.

InfluxDB 1.x defaults to port `8086`; InfluxDB 3.x defaults to port `8181`; TimescaleDB and CrateDB default to port `5432`.

**Notes while you type.** The dialog points out likely mistakes under the server fields: a scheme or path in the host (`http://`), a port typed after the host, `0.0.0.0`, a missing InfluxDB 3.x token, a local PostgreSQL that may already use port 5432, and CrateDB's HTTP port 4200. Warnings do not stop you saving; errors do. When you leave the **Address** box, a pasted URL or `host:port` is turned into the plain host name and the port, and saving does the same.

**Test connection** checks three steps in turn: the host is found, the port is open, and signing in works. If a step fails the dialog shows every step, which one failed and what to try (for example that a new CrateDB accepts the user `crate` with no password, or that nothing is listening on the port). If it works you see one line with the server version and the time it took, a list of the databases the account can see to pick from (for the databases that use a database name), and a **Details** link for the steps. **Ping** shows the version and the response time.
InfluxDB 3 connections use SQL for queries. Database and table browsing, database creation/deletion,
tag and field discovery, table deletion, queries, writes, and health checks are supported.
InfluxDB 1.x-only commands (users and privileges, continuous queries, retention policies,
server diagnostics/statistics, and `SHOW SERIES`) are disabled for 3.x connections.

![Create/Edit Connection Dialog](docs/img/ConnectionsDialog_1.png?raw=true "Create/Edit Connection Dialog")

_The **Test** button to lets you test the connection to InfluxDB using the provided connection details._

_The **Ping** button lets you ping the InfluxDB server and check response time and server version._

_Press the **Save** button to create or update the connection information._

### Connection Groups

Groups keep many servers manageable, for example separating production from development.

* **Assign a group** - Choose or type one in the **Group** field of the connection dialog. Connections without a group stay at the top level.
* **Filter** - Use the group drop-down in **Manage Connections** to show one group, **All groups**, or only connections with no group. The list also has a **Group** column.
* **Manage groups** - Press **Groups** in **Manage Connections** to create, rename or delete groups. Renaming moves its connections to the new name; deleting a group keeps its connections and makes them ungrouped. Names are case-insensitive.
* **Object Explorer** - Connected servers appear inside a folder for their group. Group folders are sorted at the top, followed by ungrouped servers.
* **Import and export** - Groups are part of the exported settings, and importing adds groups that are not already present.

![Connection Groups](docs/img/Connections_Groups.png?raw=true "Connection Groups")

### Working with TimescaleDB

Choose **TimescaleDB** as the **Version** in the connection dialog and enter the server, a user name and a password. **Database** can stay empty to list every database on the server, or name one to show only that database (use this when your account cannot connect to the `postgres` database, as on some cloud services). **Use SSL** requires an encrypted connection; without it Tickbird uses SSL when the server offers it. TimescaleDB is PostgreSQL, so connect to a server that has the `timescaledb` extension (**Create Database** installs it in new databases when your account may).

TimescaleDB's concepts appear with the same commands as InfluxDB's, under TimescaleDB's own names:

| In Tickbird (InfluxDB) | In TimescaleDB |
| --- | --- |
| Database | Database |
| Measurement | Table or hypertable (tables outside `public` show as `schema.table`) |
| Tag Keys / Tag Values | **Text Columns** and their distinct values (up to 1,000) |
| Field Keys | **Columns**: every other column with its type |
| Series | **Chunks** of a hypertable (range and compression), or the tag combinations of a plain table |
| Drop Series | **Truncate Table**: deletes all rows and keeps the table |
| Retention Policies | One policy per hypertable (`add_retention_policy`): the table and how long to keep its data |
| Continuous Queries | **Continuous Aggregates**: a named view with a time bucket, group-by columns and aggregates, plus a refresh policy |
| Run Backfill | Downsamples existing data of a time range into a table (created when missing) |
| Users and Privileges | **Roles** (administrator = superuser) and read, write or all rights on the tables of a database |
| Show Queries | Active sessions (`pg_stat_activity`); **Kill Query** cancels the statement |
| Diagnostics / Statistics | Server and extension versions, uptime and connections; database, table, session, hypertable and background job statistics |

Queries are PostgreSQL SQL, with `time_bucket()`, `first()`, `last()` and the other TimescaleDB functions in the IntelliSense list. Results can be shown as a grid, chart or map like any other result, and timestamps are shown in UTC.

More help: [TimescaleDB help](docs/help/timescaledb.md) has a query cookbook, how each tool behaves, what your account needs and troubleshooting. Sample data for trying it is in [docs/sample-data](docs/sample-data).

### Working with CrateDB

Choose **CrateDB** as the **Version** in the connection dialog. The dialog has no **Database** field (a CrateDB server has one database, `crate`, and its tables live in schemas), starts with the user `crate`, and explains that Tickbird connects over CrateDB's PostgreSQL protocol on port `5432`; the Admin UI and HTTP use `4200`, and the dialog refuses that port. A new CrateDB has the user `crate` with no password.

| In Tickbird (InfluxDB) | In CrateDB |
| --- | --- |
| Database | The one database, `crate` (cannot be created or dropped) |
| Measurement | Table or view (tables in `doc` show by name, others as `schema.table`; the server's own schemas are hidden) |
| Tag Keys / Tag Values | **Text Columns** (text and IP) and their distinct values (up to 1,000) |
| Field Keys | **Columns**: every other column with its type |
| Series | **Partitions** of a partitioned table, or the tag combinations of a plain table |
| Drop Series | **Delete All Rows** (`DELETE FROM`, as CrateDB has no `TRUNCATE`) |
| Users and Privileges | Users; **Read** is `DQL`, **Write** is `DML` and **All** adds `DDL`, for the whole cluster. CrateDB cannot make superusers with SQL, so there is no Administrator option |
| Show Queries | Running jobs (`sys.jobs`); **Kill Query** runs `KILL` |
| Diagnostics / Statistics | Node, version, uptime and cluster; nodes, tables, shards and table health |

Retention policies, continuous queries, backfill and creating or dropping a database do not exist in CrateDB, so their toolbar buttons are hidden while a CrateDB connection is selected. Queries are CrateDB SQL, with `date_bin()`, `date_trunc()` and the `sys` tables in the IntelliSense list.

More help: [CrateDB help](docs/help/cratedb.md) has a query cookbook, how each tool behaves, the differences to keep in mind and troubleshooting. Sample data for trying it is in [docs/sample-data](docs/sample-data).

## Connecting to a Server

Once you have created at least one connection, select it in the **Manage Connections** dialog and press the **Connect** buton.

![Connect to a Server](docs/img/ManageConnectionsDialog_WithLocalhost.png?raw=true "Connect to a Server")

After pressing the **Connect** button you will see the **the main application window**. The list of active connection(s) is located in the tree view to the left. This window is where perform most of your interactions with the various InfluxDB connections that you have chosen to connect to. You can launch the **Manage Connections** dialog again at any time by pressing the toolbar button in the top-left corner or by selecting from the application menu **Connections** → **Manage**.

![Main Window](docs/img/AppForm_InitialView.png?raw=true "Main Window")

_Explore databases and measurements by using the tree view in the left panel._

## Working with Connections

**Right-click** when you have a **Connection** node selected in the tree view. You will see a **context menu** of available commands for the server. These commands are also available at the top of the application in **the toolbar**. Find the corresponding **toolbar button** by matching its icon to the **context menu** command.

![Connection Commands](docs/img/Connections_ContextMenu.png?raw=true "Connection Commands")

The following connection commands are available:

 * **Refresh** - Refreshes the connections data; refetches database information from the server.
 * **Create Database** - Creates a new database on the server.
 * **Show Users** - Lists users on the server and provides a user management interface.
 * **Diagnostics** - Shows server diagnostics including runtime, version, uptime, etc.
 * **Disconnect** - Closes the connection and removes it from the active connection list.
 
### Showing Server Diagnostics

To show connection diagnostics **right-click** or use **the toolbar button** to select the **Show Diagnostics** command:

![Show Diagnostics](docs/img/Connections_Diagnostics_2.png?raw=true "Show Diagnostics")
 
### Arranging the Object Explorer

The **Object Explorer** (the connection tree) works like the tool windows in SQL Server Management Studio. Use the buttons in its title bar, or right-click the title bar:

 * **Window Position (▾)** - Choose **Float**, **Dock Left**, **Dock Right**, **Auto Hide**, or **Hide**.
 * **Auto Hide (pin)** - Collapses the pane to a tab on the window edge. Hover over or click the tab to slide it out; click elsewhere or press `Esc` to slide it back. Click the pin again to dock it.
 * **Close (✕)** - Hides the pane. Select **View → Object Explorer** or press `F8` to show it again.
 * **Drag the title bar** - Drag it away to float the pane in its own window. While dragging, drop on the left or right dock target to dock it on that side.
 * **Double-click the title bar** - Switches between floating and docked.
 * **Filter** - Type in the box above the tree (`Ctrl+E` jumps to it) to show only connections, databases and measurements whose names contain the text. The folders above each match open automatically, and a matching database shows all its measurements. Databases that were not expanded yet are loaded in the background so their measurements can match. Press `Enter` or `↓` to move to the first match and `Esc` to clear the filter.

Drag the edge of a docked pane to resize it. The position and size are remembered between sessions; **View → Reset Window Layout** restores the default (docked on the left, with the toolbar and status bar shown).

## Working with Databases

The following commands are available from the Database **context menu** and **toolbar buttons**:

  * **Refresh** - Refreshes the database data; refetches measurement/series information from the server.
  * **New Query** - Opens a new query tab where you can run custom queries and explore the results.
  * **Show Continuous Queries** - Allows you to list, create, and delete **Continuous Queries** for the database.
  * **Run Backfill** - Lets your run a **Backfill Query** that downsamples or processes data into new measurements.
  * **Drop Database** - Drops the current database from the server.

### Creating a Database 
 
To create a database, select the **Connection** where you would like to create the database and select **Create Database** from the **context menu** or **the toolbar button**. Next provide the name for the database to create and press the **Create** button:
 
![Create Database](docs/img/Connections_CreateDatabase_1.png?raw=true "Create Database")

The created database should now appear in the tree view on the left:

![Database Created](docs/img/Connections_CreateDatabase_2.png?raw=true "Database Created")

### Dropping a Database

To drop a database, select it in the tree view on the left. Then **right-click** or use **the toolbar button** and select **Drop Database**:

![Confirm Drop Database](docs/img/Databases_Drop_2.png?raw=true "Confirm Drop Database")

_Confirm that you would like to drop the selected database (**this is a permanent operation**)._

### Running a Database Query

Select a database node in the tree view and either **double-click**, **right-click**, use **the toolbar button**, or select from the application menu **Query → New** to select the **New Query** command.

Press **Ctrl+R**, **the toolbar button**, or select **Query → Run** to run the query. If query text
is selected, only the selection is executed. If there is no selection, the complete editor content
is executed. Results are displayed in the table area below:

![Run Query](docs/img/Databases_RunQuery_2.png?raw=true "Run Query")

Using **aggregation (GROUP BY)** in queries will group the series results into their own tabs in the results area:

![Group Results](docs/img/Databases_RunQuery_3.png?raw=true "Group Results")

### Using the Query Editor

The query editor supports both InfluxQL for InfluxDB 1.x and SQL for InfluxDB 3.x.

* **New query** - Select **Query → New**.
* **Open query** - Select **Query → Open Query** or press `Ctrl+O` to load a query file into the active editor.
* **Save query** - Select **Query → Save Query** or press `Ctrl+S` to save the active editor as a `.sql` file.
* **Run selected text** - Highlight one or more statements and press `Ctrl+R` to run only that text.
* **Run the full editor** - Clear the selection and press `Ctrl+R`.
* **Comment lines** - Select one or more lines and click the `--` toolbar button.
* **Uncomment lines** - Select commented lines and click the `×--` toolbar button.
* **Open IntelliSense** - Press `Ctrl+Space` to display completion suggestions. The list also opens as you type and stays open while you keep typing the word.
* **Tag value completion** - After `tag = '` (or `!=`, `<>`), the list offers the values of that tag, already quoted and escaped for the server type. Values come from the `FROM` measurement, or from up to 10 measurements that have the tag when there is no `FROM` yet. At most 1,000 values are listed per tag.
* **Syntax colors** - Keywords are blue, functions magenta, strings red, comments green, and operators and `AND`/`OR` gray, as in SQL Server Management Studio.
* **Stop a running query** - Click **■ Stop** next to the running timer, the **Stop Query** toolbar button, or **Query → Stop** (`Alt+Pause`). For InfluxDB 1.x, stopping closes the request, which also ends the query on the server.
* **Query timeout** - Queries that run longer than **Settings → Query Timeout** (30 seconds to 1 hour, or no limit; 5 minutes by default) are stopped automatically.
* **Find and replace** - Press `Ctrl+F` to find or `Ctrl+H` to find and replace in the editor; `F3` / `Shift+F3` move to the next / previous match and `Esc` closes the bar. Selected text is used as the search text. Match case, whole words and regular expressions are available in the bar.

For InfluxDB 1.x, IntelliSense includes InfluxQL statements, functions, measurement names, and
contextual field and tag names. Measurement names are loaded when the query tab opens. After a
`FROM` clause identifies a measurement, its field and tag metadata are loaded on demand. If the
connected account cannot read schema metadata, static keyword and function completion remains
available. The static InfluxQL list is loaded from `Resources/InfluxQL.xml`, making the XML file
the single editable source for both keyword and function suggestions. InfluxDB 3.x query tabs use
a SQL-oriented completion list and table metadata.

### Query History

Every query you run from a query tab is recorded with its connection, database, start time, duration, row count and outcome (OK, Error, Cancelled or Timed out). Open it from **View → Query History** or **Query → Query History** (`Ctrl+Shift+H`).

* Type in the search box to find queries by text, connection or database (all words must match), or choose a connection.
* Double-click an entry, or press `Enter`, to open the query in a new query tab on the same connection and database.
* **Copy** copies the selected queries; **Delete** (or the `Delete` key) and **Clear All** remove entries.
* **Favorites** - Select queries and press **☆ Add Favorite** to mark them with a ★ (press **★ Remove Favorite** to unmark). Tick **Favorites only** to show just those. Favorites never expire and are the last to be dropped when the 1,000-entry limit is reached, and the favorite mark is kept in export and import files.
* **Deleting with favorites** - Deleting a selection that contains favorites asks first, with a box **Also delete the favorite queries** that is unticked, so favorites are kept unless you tick it. **Clear All** always asks, and offers the same box when there are favorites; when only favorites are left it asks before deleting them.
* Running the same query again on the same database moves it to the top instead of adding a copy. The most recent 1,000 queries are kept in `query-history.json` next to the settings file. Turn recording off with **Settings → Keep Query History**.
* **Expiry** - By default queries are kept always. Choose **Settings → Query History Expiry** to remove entries automatically after 1, 7, 30, 90 or 180 days or 1 year, or pick **Always keep** to turn expiry off again. Expired entries are removed when the setting changes, when the history loads, when a query is added, and when you open the Query History tab.
* **Export** - **Export…** saves the selected queries, or all of them when none are selected, to a `.json` file.
* **Import** - **Import…** adds the queries from an exported file, so you can move your history to another computer or share it. A query that is already in the history (same connection, database and text) is not duplicated; the more recent run is kept. Entries that would already be expired under your current setting are skipped, and the 1,000-entry limit still applies.

The save and open commands are located in the **Query** menu rather than below the query editor,
leaving more vertical space for query text and results.

Drag the visible horizontal divider below the query status line to resize the query and result
areas. Both panels keep a minimum usable height.

### Working with Large Query Results

Large query results are retained in memory but displayed through a virtual list. Tickbird
creates only the rows requested for the visible area as you scroll, avoiding the large UI allocation
and rendering cost that previously caused the application to become unresponsive or terminate.

CSV and JSON export continue to operate on the complete in-memory result, including rows that have
not been displayed. **Export Selected** exports only the rows selected in the virtual result list.

### Exporting Database Query Results

The results of most query windows in Tickbird can be exported to file. **Right-click** in the results table and choose from the available export options.
Data can be exported in either **CSV** or **JSON** format. Choosing **Export All** will export the entire set of returned rows to file. Alternatively you can
export just the selected rows by using **CTRL + Left Click** and **Shift + Left Click** to select the rows you want to export and then choosing **Export Selected**
in the export context menu.

![Exporting Database Results](docs/img/Databases_ExportQueryResults.png?raw=true "Exporting Database Results")

### Filtering Query Results

Use the filter bar above the results to narrow the rows without running the query again. The **chart** and **map** show the same filtered rows, so you can, for example, map only the stretches where `operatingmode = 2`.

* **Filter rows…** - Type words to keep only rows that contain every word in some column (as displayed), or in a GROUP BY tag. Press `Esc` to clear.
* **Conditions ▾** - Add conditions on columns, joined by **AND**: `=`, `≠`, `>`, `≥`, `<`, `≤`, `contains`, `is empty`, `is not empty`. Numbers compare as numbers; text ignores case.
* **Right-click a cell** › **Filter to This Value** or **Exclude This Value** adds a condition from that cell; **Clear Filter** shows all rows again.

The status next to the filter shows how many rows are shown (for example *Showing 120 of 5,000 rows*). The `#` column keeps each row's number in the full result. Copy and export use the rows that are shown. The filter is shared by all result tabs of the query tab and is kept when the query is run again.

![Filtering Query Results](docs/img/Results_Filter.png?raw=true "Filtering Query Results")

### Inserting, Editing and Deleting Records

When a result comes from a plain `SELECT ... FROM table` (no joins, grouping or aggregates), the bar above the grid shows **＋ Insert**, **✎ Edit** and **✕ Delete**; the same commands are in the right-click menu. Every one of them asks you to confirm before anything is sent, and the query runs again afterwards so the grid shows what is stored.

* **Insert** - A form with one box per column. Empty boxes are left out.
* **Edit** - Shows the selected record; only the columns you change are saved, and the confirmation lists each change as *old → new*. An empty box saves NULL.
* **Delete** - Shows the record and asks *Delete this record?* with a warning that it cannot be undone.

How a record is found depends on the database:

* **TimescaleDB and CrateDB** - By the table's primary key. A table without one is matched on all of its columns, and if more than one identical row exists nothing is changed. The result must include the key columns (or use `SELECT *`).
* **InfluxDB 1.x** - A record is a point: its **time and tags identify it** and cannot be edited (insert a new point and delete the old one instead). Editing writes the changed fields over the point; deleting removes the whole point at that time with those tags, including fields that are not shown. Times with more precision than Tickbird can read back may not be found; the point is then reported as not found and nothing is changed.
* **InfluxDB 3.x** - Insert and Edit only. It has no delete statement, so Delete is hidden.

### Charting Query Results

Every result tab has **Grid** and **Chart** buttons in its top-right corner (you can also right-click the grid and choose **Show as Chart**). The choice is remembered for the query tab, so re-running the query keeps the chart open.

* **What gets plotted** - Each numeric (or boolean) column becomes a line against the `time` column. Text columns, including tag values that look like numbers, are not plotted; they are listed under the chart as "not plottable". Null values show as gaps.
* **InfluxDB 1.x `GROUP BY` tags** - Each tag set is its own result tab. **Combine all N results** (on by default) draws them on one chart, labelled like `mean {host=web-1}`.
* **InfluxDB 3.x SQL** - Results come back as one flat table, so tags are ordinary columns. Use **Split by** to draw one line per value of a tag column (for example `host`).
* **No time column** - Results without a time column, and InfluxQL aggregates without `GROUP BY time` (every row is stamped 1970-01-01), are drawn as bars, labelled by the first text column or by the tag set.
* **Style** - Line, Step, Points or Area. **Width** sets the width of every line. **Fields** shows or hides individual columns.
* **Colors and widths** - **Lines** picks a color scheme (Category 10, Colorblind friendly, Nord, and others) and lets you give any line its own color (from swatches, or typed as `#FF8800` or `orange`) and width. **Automatic** and **Reset all lines** go back to the scheme. Choices are kept per line label while the query tab is open, so they survive re-running the query. For bar charts the menu is called **Colors**.
* **Reading values** - Hover to see the time and every line's value at that point. Scroll to zoom, drag to pan, middle-drag to zoom into an area, and right-drag to stretch an axis. **Reset zoom** (or a double-click) fits everything again. Right-click the chart to copy or save the image.
* **Large results** - Lines with more than 5,000 points are reduced for drawing with the LTTB algorithm, which keeps peaks, dips and gaps. The status line shows the original point count. For very large ranges, `GROUP BY time(...)` is still faster.
* **Times** are shown in UTC, the same as the grid.
* **Save PNG…** exports the chart as an image.

Charts use [ScottPlot](https://scottplot.net) 5.1.58, the last release that supports Avalonia 11.

![Chart of Query Results](docs/img/Results_Chart.png?raw=true "Chart of Query Results")

### Mapping Query Results

Results that contain GPS positions can be drawn on a map. Tracks are blue unless you color them by a field, rules or per track. Select **Map** (next to **Grid** and **Chart**) above the results, or right-click the grid and choose **Show on Map**. For example:

```sql
SELECT "latitude", "longitude", "state", "speed" FROM "gps" WHERE "vehicle" = '6S2495' AND time > now() - 1d
```

Use the map toolbar to bind the result's columns:

 * **Latitude / Longitude** - The columns holding the position. Columns named like `lat`/`latitude` and `lon`/`lng`/`longitude` are chosen automatically. Rows without a usable position, including 0,0, are skipped.
 * **Color by** - A column that sets the line color. Choose how its values are used:
   * **Categories** - One color per value, for example `work` and `turn`.
   * **Ranges** - Numeric values are split into 2 to 10 equal ranges, colored green to red, for example `speed`.
   * **Colors in column** - The column already holds colors such as `#FFD400`.
   * **Rules (multiple fields)…** - Color by several fields at once. Each rule has a color and one or more conditions joined by **AND**, for example `state = work` and `speed > 3` → yellow, then `speed ≤ 3` → orange. A point takes the color of the first rule it meets (use ↑/↓ to reorder); points that meet no rule use the **Other** color. Conditions support `=`, `≠`, `>`, `≥`, `<`, `≤`, `contains`, `is empty` and `is not empty`; numbers compare as numbers and text ignores case.
 * **Colors ▾ / Rules ▾** - Pick the color of each value, range or track, or edit the color rules.
 * **Layers ▾** - Show several fields at the same time. Each layer marks the stretches of track where its condition holds (one or more conditions joined by **AND**), in its own color and style, on top of the track's colors. For example: `operatingmode = 2` as an orange **Line**, `depthguidance = on` as a green **Halo** under the track, and `bunkerunload = true` as **Points**. A point can be in several layers at once. Tick layers on or off in the map legend. **One layer per value of** a field adds a layer for each value it takes (up to 12), for example every operating mode in its own color. A map shows up to 64 layers.
 * **Width** - The line width.
 * **Track by** - A text column that draws one track per value, for example a vehicle ID. With **GROUP BY** tags, **Combine all results** draws every result set on the same map.
 * **Map** - **Hybrid**, **Satellite** (Esri World Imagery), **Streets** (OpenStreetMap), or **None**. **None** works offline; the lines are still drawn.

Drag to pan, scroll or double-click to zoom, and use **Fit** to show every track. Hover over a line to see the time, position and color value of the nearest point. **Save PNG…** saves the map with its legend. Map settings are shared by the query's result tabs and are kept when the query is run again.

![Map of Query Results](docs/img/Results_Map.png?raw=true "Map of Query Results")

### Creating Continuous Queries

[Continious Queries](http://docs.influxdata.com/influxdb/v1.1/query_language/continuous_queries/) run at an interval that you specify and are often used to downsample data by averaging it or processing it as it streams in and dumping the downsample data into a new measurement. 

To create and manage Continuous Queries, **right-click** or use **the toolbar button** and select the **Show Continuous Queries** command.

![Create Continuous Query](docs/img/Databases_CQ_2.png?raw=true "Create Continuous Query")

_Once the CQ tab opens, click the **Create CQ** button which will bring up the **Create Continuous Query Dialog**._

### Continuous Query Dialog

Use the dialog to design a new **Continuous Query**. Mouse over the **help tool tips** to learn more about the different form values.

![Create Continuous Queries Dialog](docs/img/Databases_CQ_3.png?raw=true "Create Continuous Queries Dialog")

_Once you are satisified with your CQ press the **Create** button to create and run your query. You can verify that the CQ is working by running queries against its destination measurement and exploring the data:_

![Continuous Query Created](docs/img/Databases_CQ_4.png?raw=true "Continuous Query Created")

### Running a Backfill Query
 
**Backfill Queries** are similar to **Continuous Queries** in that they are typically used to process or downsample data from one measurement into another.
The difference is that where as **Continuous Queries** run in realtime at a fixed interval and process incoming data inside a user-specified rolling time window,
**Backfill Queries** are typically only run once on older measurment data that is already stored in the database and outside of the CQ time window.

For example: imagine that you decided you needed to start downsampling data that you've already been collecting for months. You could create a **Continuous Query**
that would downsample the data every hour and average it on all new, incoming data. However since that would only begin downsampling new data, you might also consider
running a **Backfill Query** on the older historic data so that all data becomes downsampled both backwards and forwards through time.

To run a **Backfill Query** either **right-click** or click **the toolbar button** and select **Run Backfill**. The **Run Backfill Query Dialog** will open:

### Run Backfill Query Dialog

Use the dialog to design a **Backfill Query**. Mouse over the help tool tips to learn more about the different form values.

![Run Backfill Query Dialog](docs/img/Databases_Backfill_1.png?raw=true "Run Backfill Query Dialog")

Click the **Run** button when you are ready to run the backfill. Unless you receive an error message, the query has successfully run.
Check to see that the data exists in the destination measurement as expected:

![Backfill Query Results](docs/img/Databases_Backfill_2.png?raw=true "Backfill Query Results")

_If you don't see any data or your destination measurement was not created check your time ranges to make sure the **Backfill Query** did not operate on an empty result set._

## Working with Measurements and Series

The following commands are available from the Measurement **context menu** and **toolbar buttons**:

  * **New Query** - Opens a new query tab where you can run custom queries and explore the results.
  * **Show Tag Keys** - Lists all tag names/keys that exist for the selected measurement/series.
  * **Show Tag Values** - Lets you explore the values of all tags keys.
  * **Show Field Keys** - Lists the names/keys of all fields in the measurement as well as their data types.
  * **Show Series** - Lists all Series that exist under the selected measurement.
  * **Drop Measurement** - Drops the selected measurement from the database.
  * **Drop Series** - Leaves the measurement but drops all series data from it.

### Running a Measurement Query

Select a measurement node in the tree view and either **double-click**, **right-click**, use **the toolbar button**, or select from the application menu **Query → New** to select the **New Query** command.

Press **CTRL+R**, **the toolbar button**, or select from the application menu **Query → Run** to run the query. Results will be displayed in the table area below:

As with database queries, selecting text runs only the selected portion; with no selection, the
entire query editor is executed.

![Run Query](docs/img/Measurements_RunQuery_1.png?raw=true "Run Query")

Using **aggregation (GROUP BY)** in queries will group the series results into their own tabs in the results area:

![Group Results](docs/img/Databases_RunQuery_3.png?raw=true "Group Results")

### Exporting Measurement Query Results

The results of most query windows in Tickbird can be exported to file. **Right-click** in the results table and choose from the available export options.
Data can be exported in either **CSV** or **JSON** format. Choosing **Export All** will export the entire set of returned rows to file. Alternatively you can
export just the selected rows by using **CTRL + Left Click** and **Shift + Left Click** to select the rows you want to export and then choosing **Export Selected**
in the export context menu.

![Exporting Measurement Results](docs/img/Measurements_ExportQuery_1.png?raw=true "Exporting Measurement Results")

### Showing Tag keys

Select a measurement node in the tree view and either **right-click** or use **the toolbar button** and select **Show Tag Keys**:

![Showing Tag Keys](docs/img/Measurements_ShowTagKeys.png?raw=true "Showing Tag Keys")

_Tag keys can be exported by **right-clicking** in the results area and using the **export context menu**._

### Showing Tag Values

Select a measurement node in the tree view and either **right-click** or use **the toolbar button** and select **Show Tag Values**:

![Showing Tag Values](docs/img/Measurements_ShowTagValues.png?raw=true "Showing Tag Values")

_Use the **drop down menu** in the **Tag Values tab** to explore the values for each tag. Tag values can be exported by **right-clicking** in the results area and using the **export context menu**._

### Showing Field Keys

Select a measurement node in the tree view and either **right-click** or use **the toolbar button** and select **Show Field Keys**:

![Showing Field Keys](docs/img/Measurements_ShowFieldKeys.png?raw=true "Showing Field Keys")

_Field keys can be exported by **right-clicking** in the results area and using the **export context menu**._

### Showing Series

Select a measurement node in the tree view and either **right-click** or use **the toolbar button** and select **Show Series**:

![Showing Series](docs/img/Measurements_ShowSeries.png?raw=true "Showing Series")

_Series information can be exported by **right-clicking** in the results area and using the **export context menu**._

### Dropping Measurements

To drop a measurement, select it in the tree view on the left. Then **right-click** or use **the toolbar button** and select **Drop Measurement**:

![Confirm Drop Measurement](docs/img/Measurements_DropMeasurement.png?raw=true "Confirm Drop Measurement")

_Confirm that you would like to drop the selected measurement (**this is a permanent operation**)._

### Dropping Series

To drop all series for a measurement, select the measurement in the tree view on the left. Then **right-click** or use **the toolbar button** and select **Drop Series**:

![Confirm Drop Series](docs/img/Measurements_DropSeries.png?raw=true "Confirm Drop Series")

_Confirm that you would like to drop series for the selected measurement (**this is a permanent operation**). The measurement will be left intact, but all series data will be dropped._

## Confirming Destructive Actions

Dropping a **database**, **measurement** or **series** cannot be undone (see the [Disclaimer](#disclaimer)), so the confirmation dialog asks you to type the object's name before the red **Drop** button turns on. The match is exact and case-sensitive (like InfluxDB names), and `Enter` confirms once the name matches. **Cancel** or `Esc` leaves the data untouched.

## Working with Users and Privileges

The following is a list of available commands when working with users and privileges:

  * **Create User** - Creates a new user on the InfluxDB server.
  * **Edit User** - Allows a user's administrator status to be updated.
  * **Change Password** - Allows a user's password to be updated.
  * **Drop User** - Drops a user from the InfluxDB server.
  * **Grant Privilege** - Grants a privilege (Read, Write, All) to a user for a particular database.
  * **Edit Privilege** - Updates a user's privilege for a particular database.
 
### Showing Users

Select a connection node in the tree view and either **right-click** or use **the toolbar button** and select **Show Users**:

![Showing Users](docs/img/Connections_ShowUsers.png?raw=true "Showing Users")

_Users for the connection will be listed with an indicator of whether or not they have administrator privileges. Clicking on a user in the list will also let you browse and edit their database privileges._

### Managing Users

#### Creating a User

After bring up the **Users tab** by selecting the **[Show Users](#showing-users)** command, **right-click** or use the **Create User** button in the **Users tab**:

![Create User Dialog](docs/img/Connections_CreateUser_1.png?raw=true "Create User Dialog")

Enter the user's name and password and select whether or not they will be a server administrator and click the **Create** button.

![User Created](docs/img/Connections_CreateUser_2.png?raw=true "User Created")

#### Editing a User

Presently the only editable aspect of a user is whether or not they are a server administrator. To edit a user **right-click** or click the **Edit User** button in the **Users tab**:

![Edit User](docs/img/Connections_EditUser.png?raw=true "Edit User")

#### Changing a User's Password

To update a user's password, **right-click** or click the **Change Password** button in the **Users tab**:

![Change User Password](docs/img/Connections_ChangeUserPassword.png?raw=true "Change User Password")

#### Dropping a User

To drop a user, **right-click** or click the **Drop User** button in the **Users tab**:

![Confirm Drop User](docs/img/Connections_DropUser.png?raw=true "Confirm Drop User")

_Confirm that you would like to drop the selected user (**this is a permanent operation**)._

### Managing Privileges

After bring up the **Users tab** by selecting the **[Show Users](#showing-users)** command, click on a user to select them from the list. The user's various databases privileges will be listed in the **Privileges Panel** below the users list.

#### Granting a Privilege

To grant a new database privilege to a user make sure the user is selected in the users list and then **right-click** in the **Privileges Panel** or click the **Grant Privilege** button:

![Granting a Privilege](docs/img/Connections_GrantUserPrivilege_1.png?raw=true "Granting a Privilege")

Select the database the privilege will be for and then select one of the following privileges to grant:

  * **Read** - The user can only read from the database.
  * **Write** - The user can only write to the database.
  * **All** - The user can read and write to the database.

Click the **Grant** button to grant the user the privilege:

![Privilege Granted](docs/img/Connections_GrantUserPrivilege_2.png?raw=true "Privilege Granted")

#### Editing a Privilege

To edit a privilege make sure the user is selected in the users list and then **right-click** in the **Privileges Panel** or click the **Edit Privilege** button:

![Edit a Privilege](docs/img/Connections_EditUserPrivilege.png?raw=true "Edit a Privilege")

_Select the desired privilege from the drop down and save. To revoke all privileges, select the **None** option._

## Application Settings

Application settings can be adjusted in the application **Settings** menu. Settings and connection information can also be imported and exported.

Settings are saved automatically in the version-independent per-user file shown in the
[Installation](#installation) section. Settings from earlier versions are copied over automatically on
first launch, and settings from the original WinForms release can be imported with
**File → Import → From InfluxDB Studio**. Future upgrades load the stable settings file directly.

### Settings Overview
  
  * **Settings → Time Format**
    * **12 hour** - Time will be displayed in a 12 hour format with AM/PM.
	* **24 hour** - Time will be displayed in a 24 hour format.
  * **Settings → Date Format** - Applies to result grids and the query history.
    * **System default** - Follows the regional settings of your computer, for example `31.12.2016` on a German system or `12/31/2016` on a US one (the default). The menu shows today's date in that format.
    * **Month first (US)** - `12/31/2016`.
    * **Day first (UK, France, Spain, Italy)** - `31/12/2016`.
    * **German (Germany, Austria, Switzerland, Poland, Russia)** - `31.12.2016`.
    * **No zero padding (Finland, Czechia, Slovakia)** - `3.2.2016`.
    * **Dutch (Netherlands)** - `31-12-2016`.
    * **ISO 8601 (Sweden, Lithuania, logs)** - `2016-12-31`.
    The separators of the German, Dutch, ISO and no-padding formats are fixed, so they look the same whatever your computer's language.
  * **Allow Untrusted SSL** - When this setting is turned on, untrusted SSL certificates for InfluxDB servers will be allowed.
  * **Settings → Keep Query History** - Record the queries you run in the [Query History](#query-history).
  * **Settings → Query History Expiry** - How long history entries are kept: **Always keep** (the default), or 1, 7, 30, 90 or 180 days, or 1 year.

### Exporting Settings

To export application settings, including all configured InfluxDB server connections, select **File → Export → Settings** from the **application menu** and choose a location and file name for the settings file and click **Save**.

### Importing Settings

To import application settings, including all configured InfluxDB server connections, select **File → Import → Settings** from the **application menu** and browse to and select the exported settings file you would like to import then click **Open**.

## Disclaimer

The program is provided AS IS with NO WARRANTY OF ANY KIND, INCLUDING THE WARRANTY OF DESIGN, MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE.

The provider of this tool is not responsible or liable for any loss of data, or any other loss or damage, arising from its use. Back up your data before running commands that change or delete it.

The same notice is shown in **Help** → **About**.

## License

Code and documentation are available according to the *MIT* License (see [LICENSE](https://github.com/sadaqatbukhari/Tickbird/blob/master/LICENSE)).

Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
