# Plan: Detailansicht-Skelett auf weitere Seiten übertragen

Stand: 2026-10-13 · Referenz: Pilot Backup → Repositories (Commits `a573244`, `c47435e`) · Spec: `src/Kaimo_File_Server.Web/DESIGN_SPEC.md` §9.2, §10.1, §18 Nr. 5

## 1. Ziel

Die **Leseansicht** ausgewählter Listenelemente bekommt überall dasselbe Skelett wie der Pilot
(`Components/Pages/Backup/Components/BackupRepositories.razor`, Zweig `else if (Selected is { } view)`):

1. `<DetailHeader>`: Icon, Name, Untertitel, `StatusBadge`; darunter Primär- und 1–2 häufige Aktionen als `.btn-tinted`, alles Weitere im „⋯“-Menü (`<MenuItem>`), Löschen zuletzt nach Separator mit `Danger`.
2. `.detail-alerts`: ein Banner pro Problem/offenem Zustand (Fehler, Fortschritt, ausstehende Schritte).
3. `.fact-tiles` mit 2–4 `<FactTile>` (optional): „Ist alles OK?“ auf einen Blick.
4. `<DetailSection>`s mit `<PropertyRow>`s; technische IDs in einer Sektion mit `InitiallyCollapsed`.

Löschen bestätigt immer ein `modal-dialog` (statt Inline-„Wirklich löschen?“-Buttons).

## 2. Abgrenzung

- **In Scope:** jede **Leseansicht** eines ausgewählten Listenelements, egal ob es dazu eine Edit-Ansicht gibt. Bei Seiten mit Edit-Ansicht wird nur der **View-Zweig** umgebaut.
- **Edit-Zweige bleiben unverändert** (Formulare, Tabs im Edit-Modus, Speichern/Abbrechen). Einzige Berührung: Einstieg „Bearbeiten“ kommt künftig aus dem `DetailHeader`.
- **Out of Scope:** Mischformen, bei denen Detail und Bearbeitung in einer Ansicht liegen (Felder direkt editierbar, Speichern pro Feld/Bereich), wie Shares.
- Keine Änderungen an ViewModels/Services, außer wo eine Kennzahl für eine FactTile fehlt (z. B. `DateFormatService`-Hilfen) oder ein Navigations-Callback (`OnLink`) durchgereicht werden muss.

## 3. Inventar

### 3.1 In Scope: Leseansicht mit getrennter Edit-Ansicht

| # | Seite / Komponente | Heutiger View-Aufbau | Aufwand |
|---|---|---|---|
| A | `Pages/Backup/Components/BackupJobs.razor` | `workspace-heading`, `workspace-dl`, Fortschritt, `detail-actions`, Inline-Löschbestätigung | klein |
| B | `Pages/ExternalStorage/Components/SyncAdministration.razor` | `<h3>` + 2–3 `<dl>`-Gruppen, „Jetzt ausführen“ + Inline-Ergebnis, Fehler-Banner, Fehlerliste | mittel |
| C | `Pages/ExternalStorage/Components/ConnectionAdministration.razor` | `<h3>` + 3 `<dl>`-Gruppen (Identität, Zustand, Nutzung), Test + Inline-Ergebnis, Autorisieren, Fehler-Banner | mittel |
| D | `Pages/ShareLinks/ShareLinkList.razor` | eigenes `sharelink-detail-*`-Grid, 3 Gruppen, URL + Kopieren, Passwort-/Upload-Hinweise | mittel |
| E | `Pages/Users/Components/GroupDetailsPanel.razor` | `detail-header` + `detail-field-grid`, Inline-Löschbestätigung | klein |
| F | `Pages/Users/Components/RoleDetailsPanel.razor` | `detail-field-grid` (Berechtigungen) + delegierte Zuweisungen mit Inline-Hinzufügen/Entfernen | mittel |
| G | `Pages/Users/Components/UserDetailsPanel.razor` | eigenes `user-detail-shell--viewing` mit Avatar, Rollen, delegierten Rollen, Abteilungen, Gruppen | groß |
| H | `Pages/Departments/Departments.razor` | `detail-header` + View-Tabs (Allgemein / Berechtigungen / Zuordnungen), `detail-field-grid` | groß |

### 3.2 In Scope: Leseansicht ohne Edit-Ansicht

| # | Seite | Bemerkung |
|---|---|---|
| I | `Pages/Backup/Components/BackupActivity.razor` | Lauf-Details, Quellen-Liste, Restore-Details |
| J | `Pages/Notifications/Notifications.razor`, Tab „Protokoll“ (`notif-log-detail`) | Zustellung + Vorschau; Aktion „Erneut senden“ |
| K | `Pages/Files/HomesAdminPage.razor` | Home-Ordner; Aktivieren/Deaktivieren, Links, Löschen per Modal |

### 3.3 Out of Scope (Mischform oder kein Listen-Detail)

| Seite | Grund |
|---|---|
| `Pages/Shares/Components/ShareSettingsPanel.razor` | Mischform (laut Vorgabe) |
| `Pages/CloudAccess/CloudAccess.razor` (inkl. Virtual Shares) | Mischform: Name/Ordner/Abteilung direkt editierbar mit Speichern pro Feld |
| `Pages/CloudAccess/CloudAccessAclEditor.razor` | Inline-Editor für Berechtigungen |
| `Pages/Notifications/Notifications.razor`, Tabs „Regeln“, „Vorlagen“, „Layout“ | Immer im Bearbeitungsmodus (Regel-Editor, Template-Editor) |
| `Pages/Backup/Components/BackupRestore.razor` | Datei-Browser/Werkzeug, kein Objekt-Detail |
| `Pages/Settings/Components/StorageSettings.razor` | Einstellungsseite mit Inline-Umbenennen, keine Liste-plus-Detail |
| `Pages/Settings/Components/ClientDeviceSettings.razor` | Mischform: Umbenennen-Feld dauerhaft im Detail |
| `Pages/Settings/Components/LogViewer.razor` | Aufklappbare Log-Zeilen in der Liste, kein Detailbereich |
| `Pages/Acl/AclEditor.razor` | Editor für Berechtigungen |

## 4. Übersetzungsregeln (alt → neu)

| Alt | Neu |
|---|---|
| `workspace-heading` / `detail-header` / `detail-panel-header` / `<h3>` + Untertitel + `StatusBadge` | `<DetailHeader Icon Title Subtitle>` + `<Status>` |
| „Bearbeiten“-Button | `Actions`: `.btn-tinted` (Akzent), Icon `edit.svg`; immer erster Button |
| Ausführen/Testen („Jetzt ausführen“, „Testen“, „Wartung“) | `Actions`: `.btn-tinted--info`; läuft etwas: stattdessen „Abbrechen“ als `.btn-tinted--danger` |
| Seltene Aktionen (Aktivieren/Deaktivieren, Herunterladen, Autorisieren, Kopieren-URL …) | `MoreActions` → `<MenuItem>`; ist eine davon häufig, als zweiter sichtbarer Button (§15: wichtige Funktionen bleiben sichtbar) |
| Löschen (`btn-danger-outline` + Inline-Bestätigung) | `<MenuItem Separator />` + `<MenuItem Danger>`; Bestätigung im `modal-dialog`; Sperrgrund via `Disabled` + `Tooltip` |
| `error-banner`, Fortschrittsbalken, Hinweise auf offene Schritte | in `.detail-alerts` direkt unter dem Header |
| Inline-Erfolg/-Fehler nach Test/Lauf (`_testSucceeded`, `_runSucceeded` …) | Erfolg: Toast; Fehler: Banner in `.detail-alerts` |
| Zeitpunkte „zuletzt / nächster“, Zähler, Zustandsdatum | `<FactTile>` (`Compact` für Datum, `Tone` + `Tooltip` statt Wert + Hinweis), max. 4 |
| Verweis auf andere Bereiche (Anzahl Jobs, Repository, Verbindung) | `FactTile` mit `OnLink` oder `PropertyRow` mit Link-Inhalt |
| `workspace-dl`, `<dl>`-Gruppen, `detail-field-grid`/`detail-field-value` | `<DetailSection Title>` + `<PropertyRow>` |
| Pfade, URLs, IDs, Hashes | `PropertyRow Mono Copyable` |
| Listen (Mitglieder, Gruppen, Rollen) als Chips | `PropertyRow` mit `ChildContent` (Chips bleiben), oder eigene `DetailSection` bei vielen Einträgen |
| Ja/Nein-Werte | `Web_Common_Yes` / `Web_Common_No` (wie im Pilot) |
| Leerer Wert, „—“ | weglassen, `PropertyRow` zeigt „–“ |

**Tabs in der Leseansicht** (Departments): Laut §9.2 nur für sehr große Objekte. Entscheidung: zunächst durch `DetailSection`s ersetzen und danach bewerten; Tabs nur zurückholen, wenn die Ansicht zu lang wird.

**Unter-Sammlungen mit eigener Verwaltung in der Leseansicht** (delegierte Zuweisungen bei Rollen/Benutzern): Keine Mischform im Sinne von §2, da das Objekt selbst nicht bearbeitet wird. Entscheidung: eigene `DetailSection`, Inhalt (Liste + „+“-Button + Inline-Formular) bleibt funktional gleich und wird nur optisch eingepasst.

## 5. Nötige Erweiterungen der Shared-Komponenten

Klein halten und erst einbauen, wenn die betroffene Seite dran ist.

1. **`DetailHeader`: führendes Element frei belegbar.** Benutzer brauchen den Avatar statt eines SVG-Icons, Freigabelinks ein farbiges Icon je Zustand (`IconTone`), Abteilungen ggf. ihre Farbe. Vorschlag: optionales `RenderFragment? Leading`, das `Icon` ersetzt, plus CSS für ein rundes Avatar-Tile.
2. **`FactTile`/`PropertyRow`**: voraussichtlich nichts nötig. Falls eine Tile eine Navigation braucht, die nicht über ein `EventCallback` der Eltern-Seite geht, zuerst den Callback hochreichen (wie `OnOpenJobs` in `Backup.razor`).
3. **Lösch-Modal**: Das Muster wiederholt sich auf jeder Seite (Titel, Hinweistext, Abbrechen/Löschen). Nach Seite 2–3 entscheiden, ob ein `<ConfirmDialog>` in `Components/Shared/` lohnt; vorher nicht abstrahieren.

## 6. Pro Seite

### A · BackupJobs
- Header: Icon `history.svg` (oder passend), Titel `job.Name`, Untertitel Repository-Name, Status `JobTone`/`JobStatusLabel`.
- Actions: Bearbeiten; „Jetzt ausführen“ (`--info`, deaktiviert wenn Job aus) bzw. „Abbrechen“ (`--danger`) während Lauf.
- More: Löschen (Modal mit `Web_Backup_Jobs_DeleteHint`).
- Alerts: aktiver Lauf (Fortschritt wie Pilot), letzter Fehler falls vorhanden.
- Tiles: Letzter Lauf (Tone nach Ergebnis), Letzter Erfolg, Nächster Lauf (alle `Compact`), Repository (Link → Tab Repositories; Callback `OnOpenRepository` in `Backup.razor` ergänzen).
- Sektionen: **Quellen** (Shares, Pools, Shares im Pool, Versionen, Inhalt, Papierkorb) · **Zeitplan & Aufbewahrung** (Zeitplan, Aufbewahrung, Warnung nach Tagen).

### B · SyncAdministration
- Header: Name, Untertitel Verbindung, Status `SyncStateTone`.
- Actions: Bearbeiten; „Jetzt ausführen“ (`--info`). More: Löschen (Modal).
- Alerts: `LastErrorCode`-Banner; Liste fehlgeschlagener Dateien als `warning-banner` mit aufklappbarer Liste.
- Tiles: Letzter Lauf, Zeitplan an/aus (oder nächster Lauf, falls verfügbar), ggf. Fehleranzahl.
- Sektionen: **Synchronisation** (Verbindung, lokale Freigabe, lokaler Pfad `Mono`, entfernter Pfad `Mono`, Modus) · **Beschreibung** (nur wenn gesetzt).
- `_runSucceeded`-Inline-Meldung → Toast.

### C · ConnectionAdministration
- Header: Name, Untertitel Provider, Status `StateTone`.
- Actions: Bearbeiten; „Testen“ (`--info`); „Autorisieren/Neu autorisieren“ sichtbar, solange Zustand ≠ Ready, sonst im Menü.
- More: (Neu) Autorisieren, Separator, Löschen (`Disabled` + Tooltip wenn `usage.TotalCount > 0`, Modal).
- Alerts: Fehler-Banner; Testfehler als Banner, Testerfolg als Toast.
- Tiles: Zuletzt verifiziert (Tone nach Zustand), Syncs (Link → Tab Syncs), Virtuelle Freigaben.
- Sektionen: **Identität** (Autorisierung, Konto, Tenant, Scopes `Mono`) · **Zustand** (Profil, Geheimnisse) · **Fähigkeiten** · ggf. **Technisch** (IDs, `InitiallyCollapsed`).

### D · ShareLinkList
- Header: `Leading` mit farbigem Icon (§5.1), Titel `DisplayName`, Untertitel `ShareName · CreatedBy`, Status.
- Actions: Bearbeiten; „Link kopieren“ (häufigste Aktion, sichtbar). More: Löschen (Modal).
- Alerts: Upload-Links global deaktiviert, Passwortschutz-Hinweis (falls als Hinweis gedacht, sonst `PropertyRow`).
- Tiles: Ablauf (Tone: abgelaufen/bald/ok), Zugriffe bzw. hochgeladene Menge.
- Sektionen: **Ziel** (Freigabe, Pfad `Mono`) · **Öffentlicher Link** (URL `Mono Copyable`, Passwort ja/nein) · **Grenzen** (Start, Ende, Zugriffe, Rate, max. Dateigröße, erlaubte Typen).
- `sharelink-detail-*`-CSS danach entfernen.

### E · GroupDetailsPanel
- Header: Icon `users.svg`, Name, Untertitel „Gruppe“.
- Actions: Bearbeiten. More: Löschen (Modal).
- Sektionen: **Allgemein** (Abteilung) · **Mitglieder (n)** (Chips via `ChildContent`).

### F · RoleDetailsPanel
- Header: Icon `shield.svg`, Name, Untertitel System-/Eigene Rolle; für Systemrollen kein Bearbeiten/Löschen (wie heute).
- Actions: Bearbeiten. More: Löschen (Modal).
- Sektionen: **Berechtigungen** (Liste) · **Delegierte Zuweisungen** (bestehende Liste + Hinzufügen-Formular, siehe §4 Unter-Sammlungen).

### G · UserDetailsPanel
- Header: `Leading` = Avatar (§5.1), Name, Untertitel `@username`, Status aktiv/deaktiviert.
- Actions: Bearbeiten (wenn `CanEditSelectedProfile`). More: Löschen (wenn `CanDeleteSelectedUser`, Modal).
- Tiles (optional): Letzte Anmeldung, Home-Ordner-Größe, falls verfügbar; sonst weglassen.
- Sektionen: **Profil** (Vor-/Nachname, E-Mail, …) · **Rollen** · **Delegierte Rollen** (mit Entfernen-Button wie heute) · **Abteilungen** · **Gruppen** (jeweils Chips, „+n weitere“-Logik beibehalten).
- `user-detail-shell--viewing`-CSS in `UserList.razor.css` danach aufräumen; Edit-Shell bleibt.

### H · Departments
- Header: Icon/Farbtile der Abteilung (ggf. `Leading`), Name, Untertitel (Elternabteilung oder „Global“).
- Actions: Bearbeiten (nicht bei globaler Abteilung). More: Löschen (Modal, nicht bei globaler Abteilung).
- View-Tabs → Sektionen: **Allgemein** (Elternabteilung, Farbe, …) · **Berechtigungen** (Standard-Dateiberechtigung, „geerbt von …“ als `PropertyRow`) · **Mitglieder** · **Gruppen** · **Freigaben**.
- `_activeTab` bleibt für den Edit-Modus erhalten.

### I–K · Leseansichten ohne Edit
Gleiche Regeln. BackupActivity: Tiles Start/Ende/Dateien/Daten, Sektionen Quellen + Wiederherstellung. Protokoll: Actions „Erneut senden“ nur bei Fehler, Sektion Zustellung, Vorschau als eigene Sektion. Homes: More Löschen (Modal existiert bereits), Sektionen Ordner + Links.

## 7. Reihenfolge

1. **Phase 1, Backup fertigstellen:** A (BackupJobs), I (BackupActivity). Gleiche Seite und gleiche Daten wie der Pilot, geringes Risiko.
2. **Phase 2, External Storage:** C (Verbindungen), dann B (Syncs). Ähnliche Struktur; Test/Ausführen-Ergebnisse vereinheitlichen.
3. **Phase 3, Benutzerverwaltung:** E (Gruppen), F (Rollen), dann G (Benutzer) inkl. `DetailHeader.Leading`.
4. **Phase 4:** D (Freigabelinks), H (Abteilungen), J (Benachrichtigungs-Protokoll), K (Home-Ordner).
5. **Abschluss:** Legacy-CSS entfernen, soweit nicht mehr verwendet. `.workspace-dl` (nach Phase 1 + 4 frei); `detail-field-grid`/`detail-field-value` werden von Edit-Formularen (`UserDetailsPanel` Edit, `StorageSettings`) weiter genutzt, daher **nur** die View-spezifischen Regeln löschen. DESIGN_SPEC §18 Nr. 5 aktualisieren bzw. streichen und §9.2 „Legacy detail panels“ kürzen.

Jede Phase ist für sich auslieferbar; nach jeder Seite §18 Nr. 5 nachziehen (migrierte Seiten aus der Liste nehmen).

## 8. Checkliste pro Seite

- [ ] Alle bisherigen Werte und Aktionen sind noch erreichbar (Vorher/Nachher-Liste abhaken).
- [ ] Berechtigungen/Sichtbarkeit wie zuvor (`CanManage…`, `IsReadOnlyDemo`, Systemobjekte).
- [ ] Edit-Zweig unverändert; „Bearbeiten“ öffnet ihn wie zuvor, Abbrechen kehrt in die neue Leseansicht zurück.
- [ ] Löschen nur per Modal; Sperrgrund als Tooltip am deaktivierten Menüeintrag.
- [ ] Neue Texte in `Resources.resx` **und** `Resources.de.resx` (Sektionstitel, Modal-Titel/-Hinweis).
- [ ] Hell/Dunkel, Breite ≤ 760 px (gestapelte Rows) und ≤ 480 px geprüft.
- [ ] Tastatur: Menü mit Esc schließbar, Fokus zurück auf „⋯“; Sektionen per Tastatur auf-/zuklappbar.
- [ ] Ungenutzte seitenspezifische CSS-Klassen entfernt.

## 9. Entscheidungen (2026-10-13)

1. Jede Leseansicht wird migriert, auch ohne Edit-Ansicht (I–K gehören dazu).
2. Departments: View-Tabs werden zu Sektionen; nach dem Umbau bewerten.
3. Delegierte Zuweisungen bleiben als eigene Sektion mit Inline-Verwaltung.

## 10. Umsetzungsstand (2026-10-13)

Alle Seiten aus §3.1 und §3.2 sind migriert (A–K); jede baut fehlerfrei. Im Browser ist noch nichts geprüft (siehe §8).

**Shared-Komponenten**
- `DetailHeader.Leading`: ersetzt die Icon-Kachel (Avatar mit `.detail-head-icon--round`, farbiges Provider-/Datei-/Abteilungs-Icon).
- `DetailSection.Body`: freier Inhalt unter den Zeilen (Listen mit eigenen Aktionen, Log, aufklappbare Fehlerlisten).
- Neues Icon `play.svg` für „Jetzt ausführen“.
- Kein gemeinsamer `<ConfirmDialog>`: Das Lösch-Modal steht jetzt auf rund zehn Seiten fast gleich da; ein eigener Schritt kann es zusammenfassen.

**Verhalten, das sich ändert**
- Löschen bestätigt jetzt überall ein Modal. Verbindungen, Syncs und Freigabelinks wurden vorher **ohne** Rückfrage gelöscht.
- Test/Ausführen: Erfolg als Toast, Fehlschlag als Banner (Verbindungen, Syncs).
- Home-Ordner: Der Ein/Aus-Schalter im Detail ist jetzt die Aktion „Home-Ordner aktivieren/deaktivieren“ im Header.
- Rollen: Berechtigungen stehen gruppiert je Bereich statt als gekürzte Tag-Liste mit „+n“.
- Backup-Auftrag: „Letzter Erfolg“ wird gelb, sobald der Scheduler den Auftrag als überfällig zählt (`BackupSchedulerService.IsStale`, dafür jetzt `public`).

**Aufgeräumt**
- Seitenspezifisches View-CSS (Verbindungen, Syncs, Freigabelinks, Benutzer, Abteilungen, Protokoll, Home-Ordner) sowie global `.detail-panel-*`, `.inherited-hint`, `.permission-tag--more`.
- `.workspace-dl` bleibt, weil der Regel-Editor der Benachrichtigungen es noch nutzt (DESIGN_SPEC §18 Nr. 5).

**Offen**
- Sichtprüfung je Seite (Checkliste §8), vor allem Hell/Dunkel und schmale Breiten.
- Departments: Sektionen statt Tabs bewerten (Entscheidung §9 Nr. 2).
- Ungenutzte Texte `Web_Role_ShowAllPermissions` / `Web_Role_ShowFewerPermissions` stehen noch in den resx-Dateien.
