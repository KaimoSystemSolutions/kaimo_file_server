# ViewModel-Regressionstests — Konzept

Ziel: Der Fileserver soll gegen stille Regressionen abgesichert sein. Die ViewModels
sind das Scharnier zwischen UI und Persistenz — ihre Methoden behaupten, Shares
anzulegen, Nutzer umzubenennen, Flags umzuschalten, ACLs zu setzen. Genau hier
entstehen die teuersten Fehler: eine Methode meldet Erfolg, **ohne den Wert wirklich
zu speichern** (oder speichert den falschen Wert).

## Zwei Testebenen

| Ebene | Frage | Werkzeug | Beispiele |
|-------|-------|----------|-----------|
| **1 — Unit (Mock)** | Logik, Gates, Validierung, Fehler-Mapping | Moq | `AclEditorViewModelAuthorizationTests`, `FileBrowserViewModelAclScopeTests`, `SettingsViewModelSmbTests` |
| **2 — Integration (echte DB)** | Ändert die Methode **tatsächlich** den Wert im Store? | echte EF-Repos über Sqlite in-memory | `ShareListViewModelDatabaseTests`, `DepartmentViewModelDatabaseTests`, `UserListViewModelDatabaseTests` |
| **3 — Lifecycle (End-to-End)** | Berechtigung entziehen → wird der Zugriff **wirklich** verweigert? | echter `AclService` / `ManagementAuthService` über echte DB | `AclPermissionLifecycleDatabaseTests`, `ManagementPermissionLifecycleDatabaseTests` |

Ebene 3 ist der stärkste Sicherheits-Regressionsschutz: sie mutiert eine Berechtigung
(ACL-Eintrag löschen, Deny setzen, Scoped-Assignment entziehen, Rollen-Recht strippen)
und prüft danach mit dem **echten** Autorisierungsdienst, dass sich die Zugriffs­entscheidung
korrekt geändert hat — nicht bloß, dass eine Zeile verschwand.

> ⚠️ `ManagementAuthService` erzeugt (anders als `AclService`) **keine** eigenen DI-Scopes,
> sondern nutzt die injizierten Repos direkt. Nach einem `Update`, das der Service via
> `GetByIdAsync` bereits getrackt gelesen hat, muss für den Nach-Mutations-Check ein
> **frischer** Service gebaut werden (`BuildManagementAuthService()`) — genau wie in
> Produktion pro Request neue scoped Repos entstehen. Löschungen (Query-basiert) sind auch
> ohne Neubau sichtbar.

Ebene 1 ist schnell und prüft Verhalten (`Verify(..., Times.Once)`, `ErrorMessage`,
Rückgabewert). Sie kann aber **nicht** beweisen, dass ein Wert korrekt landet — ein Mock
akzeptiert jeden Aufruf. Diese Lücke schließt Ebene 2.

## Warum echtes Sqlite (nicht der EF-InMemory-Provider)

Sqlite ist relational und erzwingt dieselben Regeln wie die Produktion: Unique-Indizes,
Pflichtspalten, die TPC-Identity-Hierarchie, Cascade-Deletes. Der InMemory-Provider
ignoriert das und würde falsche Sicherheit vortäuschen.

## Der Kern-Trick: Rücklesen durch einen *frischen* Context

```
Seed → SUT-Methode ausführen → NewContext() → Zeile erneut laden → Wert prüfen
```

`DatabaseTestBase.NewContext()` liefert einen **neuen** DbContext auf **derselben**
offenen Sqlite-Verbindung. Nie den Context der Mutation für Assertions verwenden — der
würde EF's Change-Tracker-Cache lesen und selbst dann grün sein, wenn nichts persistiert
wurde. Ein frischer Context beweist den echten Round-Trip in den Store.

Das bildet außerdem die Produktion nach: Repos wie `ShareRepository` erzeugen pro
Operation einen neuen Context via `IDbContextFactory` — genau das macht
`TestDbContextFactory`.

## Harness (`Infrastructure/`)

- **`TestDbContextFactory`** — `IDbContextFactory<ApplicationDbContext>` über eine
  geteilte, offene Verbindung. So sehen alle Contexts dieselbe In-Memory-DB.
- **`DatabaseTestBase`** — öffnet die Verbindung, `EnsureCreated()`, stellt bereit:
  - `NewContext()` für Assertions,
  - Fabrikmethoden für **echte** Repos (`ShareRepo()`, `UserRepo()`, …),
  - Seed-Helfer (`SeedUser`, `SeedShare`, `SeedDepartment`),
  - Auth-Doubles (`AuthStateFor`, `UserContextFactoryFor`, `ContextFor`).

## Was gemockt bleibt — und warum

Autorisierung (`IManagementAuthService`), Actor-Auflösung (`IUserContextFactory`) und
das Dateisystem (`IStorageEngine`) werden gemockt. Sie haben eigene Test-Suites
(`ManagementAuthServiceTests`, `AclServiceTests`, `FileSystemStorage*Tests`). Dadurch
isoliert jeder DB-Test die eine Frage: *„Persistiert die ViewModel-Methode den richtigen
Wert?"* — statt versehentlich die Autorisierung mitzutesten.

## Neuen ViewModel-DB-Test hinzufügen

```csharp
public class MyViewModelDatabaseTests : DatabaseTestBase
{
    [Fact]
    public async Task DoThing_PersistsChange()
    {
        var actor = SeedUser("admin");
        var seeded = SeedShare("docs");
        var sut = /* new MyViewModel(EchteRepo(), gemockteAuth, ...) */;

        await sut.DoThingAsync();

        await using var db = NewContext();
        Assert.Equal(erwartet, (await db.Shares.FindAsync(seeded.Id))!.Feld);
    }
}
```

## Abdeckungsstand

- ✅ ShareListViewModel — Toggle (enabled/recycle/hidden), Rename, Delete, Create-Validierung, Load-Scope
- ✅ DepartmentViewModel — Create, Save (Felder/Bitmask/Members/Shares), Delete + Re-Assign, Global-Schutz
- ✅ UserListViewModel — Create User/Group/Role, Save (Profil/Passwort/Rollen/Members/Permissions), Delete, Scoped Assignments
- ✅ AclEditorViewModel, FileBrowserViewModel, SettingsViewModel — bestehende Mock-Suites (Ebene 1)
- ✅ Permission-Lifecycle (Ebene 3) — ACL grant/revoke/deny/inherit (Einzel **und** `HasAccessBatchAsync`) + Management-Delegation (scoped assignment / role-Recht) gegen echte Autorisierung
- ⬜ ShareBrowserViewModel, LoginViewModel — Load-/Mapping-Logik, bislang nur indirekt abgedeckt

## Beim Testen aufgefallen (nicht behoben — separate Aufgabe)

`ShareListViewModel` enthält halbfertige Methoden, deren „echter Wert" nicht sinnvoll
testbar ist, weil die Persistenz fehlt:

- `CreateShareAsync` erzeugt `rootMeta` (FileMetadata), **speichert es aber nie** und legt
  danach eine `AccessEntry` mit `FileMetadataId = rootMeta.Id` an → verwaiste ACL / möglicher
  FK-Bruch. Deshalb decken die Tests hier nur Validierung + Duplikat ab, nicht den Happy-Path.
- `GrantAccessAsync`, `RevokeAccessAsync` haben leere `try`-Blöcke (No-Op).
- `HasAccess(...)` gibt konstant `true` zurück.

Diese sollten fertig implementiert und dann per DB-Test abgesichert werden.
