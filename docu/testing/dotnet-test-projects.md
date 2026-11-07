# .NET test projects

The .NET tests are split into one test project per production project, plus a shared helper library.
Each project builds its own test assembly, so `dotnet test` runs them as separate processes, in
parallel, with their own environment variables and static state.

| Project | Tests for | Notes |
|---|---|---|
| `tests/Kaimo_File_Server.Tests.Common` | (no tests) | Shared helpers: `DatabaseTestBase` (Sqlite), `PostgresTestBase` + `[PostgresFact]`, `TestDbContextFactory`, `MockFileVersionRepository`, `MutableTimeProvider` |
| `tests/Kaimo_File_Server.Core.Tests` | `src/Kaimo_File_Server.Core` | |
| `tests/Kaimo_File_Server.Infrastructure.Tests` | `src/Kaimo_File_Server.Infrastructure` | Also references SmbBridge (home folders through the bridge) and Web (`DataProtectionCredentialVault`, the only `ICredentialVault`, and the backup repository form) |
| `tests/Kaimo_File_Server.Search.Tests` | `src/Kaimo_File_Server.Search` | `TestData/` holds sample documents for the content provider |
| `tests/Kaimo_File_Server.SmbBridge.Tests` | `src/Kaimo_File_Server.SmbBridge` | |
| `tests/Kaimo_File_Server.Web.Tests` | `src/Kaimo_File_Server.Web` | bUnit component tests; `VIEWMODEL_TESTING.md` describes the view-model test levels |

`src/Kaimo_File_Server.Host` has no tests of its own.

## Layout rules

- A test goes into the project that contains the class under test, not the project of its helpers.
- Inside a project, folders mirror the production folder of the class under test
  (e.g. `Infrastructure.Tests/Backup`, `Web.Tests/ViewModels/Files`). Exceptions:
  - `Core.Tests/Services/Files` (production: `Services/File`) — a namespace segment named `File`
    would hide `System.IO.File`.
  - `Web.Tests/Pages` is flat — sub-namespaces named after pages (`Settings`, `HomeFolders`, …)
    would hide the page components of the same name.
  - `Web.Tests/Guards` holds tests that scan source and resource files (hard-coded UI text, ARIA
    attributes, theme contrast, terminology, design alignment). They are fast and need no services.
  - `Web.Tests/Security` holds the cross-view-model security regression suites
    (`ScopedAdminEscalationDatabaseTests`, `AclPermissionLifecycleDatabaseTests`).
- The namespace follows the folder: `Kaimo_File_Server.<Project>.Tests.<Folder>`.
- `tests/Directory.Build.props` holds the shared settings and packages; every test project
  references `Tests.Common` and has a global `using Kaimo_File_Server.Tests.Common;`.
- Production projects grant `InternalsVisibleTo` to the test assemblies that need their internals.

## Running

All tests:

```bash
dotnet test Kaimo_File_Server.slnx
```

One project:

```bash
dotnet test tests/Kaimo_File_Server.Web.Tests
```

One folder (namespace):

```bash
dotnet test tests/Kaimo_File_Server.Web.Tests --filter "FullyQualifiedName~Kaimo_File_Server.Web.Tests.Guards"
```

In Visual Studio the Test Explorer groups the tests by project and namespace; each group can be run on its own.
