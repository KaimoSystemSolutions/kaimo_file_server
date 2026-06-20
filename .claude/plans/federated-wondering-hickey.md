# Konfigurierbares Kontextmenü (per-Kategorie, global)

## Context

Das Kontextmenü im FileBrowser wird aktuell **fest verdrahtet** in
`components/ContextMenu.razor` → `BuildContextMenuItems()` zusammengebaut: welche
Befehle erscheinen, in welcher Reihenfolge und mit welchen Trennern ist im Code
hartcodiert. Es existiert zwar bereits eine saubere Trennung über
`ContextMenuBuilder` / `ContextMenuItem`, aber keine Möglichkeit, das Menü ohne
Code-Änderung anzupassen.

Ziel (laut Nutzer): Ein Admin soll sich das Kontextmenü **per GUI in den
Settings** selbst zusammenstellen können – wie ein anpassbares Ribbon. Es gibt
einen Katalog verfügbarer Befehle, die man global anordnen/aktivieren kann.

**Abgestimmte Entscheidungen:**
- **Pro Kategorie konfigurierbar** (nicht pro Endung): getrennte Menüs für
  *Ordner, Archiv, Bild, Video, Audio, Dokument, Sonstige Datei,
  Mehrfachauswahl, Leerer Bereich*. Basis ist die vorhandene
  `PreviewKind`/`IsArchive`/`IsDirectory`-Erkennung.
- **Kontextregeln bleiben erhalten**, aber überwiegend über die Kategorie
  abgebildet. Restliche Laufzeit-Guards bleiben: `extract` in Mehrfachauswahl
  nur wenn *alle* Archive sind; `permissions` nur für Admins.
- **Global** (systemweit), persistiert über `IConfigRepository`.
- Bearbeitung gated über bestehende Berechtigung **`ManageSystemSettings`**
  (kein neues Permission-Flag), neuer Tab in der bestehenden Settings-Seite.

## Architektur

Trennung in drei Schichten:

1. **Katalog (Metadaten, ohne Aktionen)** – wird von Settings-GUI *und*
   Menü-Renderer geteilt.
2. **Layout (gespeicherte Konfiguration)** – pro Kategorie geordnete Befehls-IDs.
3. **Renderer/Aktionen** – bleibt in `ContextMenu.razor`, mappt ID → konkrete
   `Func<Task>`.

### Neue Dateien (in `src/Kaimo_File_Server.Web/DynamicHelpers/`)

**`ContextMenuScope.cs`** – Enum + Resolver:
```csharp
public enum ContextMenuScope
{ Background, Folder, Archive, Image, Video, Audio, Document, OtherFile, MultiSelection }
```
- `ResolveScope(HashSet<FileMetadata> selected)`:
  - 0 Items → `Background`; >1 → `MultiSelection`
  - 1 Item: `IsDirectory`→`Folder`; `FileHelper.IsArchive`→`Archive`; sonst
    `FileHelper.GetPreviewKind` mappen: Image→Image, Video→Video, Audio→Audio,
    Pdf/Docx/Text→Document, Unsupported→OtherFile.

**`ContextCommand.cs`** – Katalog-Deskriptor + statischer Katalog:
```csharp
public record ContextCommand(
    string Id, Func<string> Label, string Icon,
    bool IsDanger = false, SvgOptions? IconOptions = null,
    ContextMenuScope[] ValidScopes = null);
```
- `Label` als `Func<string>` (=> `Resources.Context_Menu_*`), damit die Kultur
  zur Laufzeit aufgelöst wird (siehe resx-Memo: keine `static readonly`
  Culture-Einfrierung).
- Statische `ContextCommandCatalog.All` mit allen Befehlen + `ById(id)`-Lookup.
- Befehle (Labels existieren bereits als `Resources.Context_Menu_*`):
  `open`, `extract`, `compress.zip`, `compress.targz`, `rename`, `delete`,
  `permissions`, `newfolder`, `refresh`.
- `ValidScopes` schränkt ein, was die GUI je Kategorie anbietet, z.B.:
  - `compress.*`: Folder + Image/Video/Audio/Document/OtherFile + MultiSelection
    (nicht Archive/Background)
  - `extract`: Archive + MultiSelection
  - `rename`/`permissions`: alle Einzel-Kategorien (Folder/Archive/*File)
  - `delete`: alle außer Background
  - `open`: alle Einzel-Kategorien
  - `newfolder`/`refresh`: alle Kategorien (auch Background)

**`ContextMenuConfig.cs`** – serialisierbare Konfiguration:
```csharp
public class ContextMenuConfig
{ public Dictionary<string, List<string>> Menus { get; set; } = new(); }
```
- Key = `ContextMenuScope`-Name, Value = geordnete Befehls-IDs.
- `static ContextMenuConfig Default()` bildet das **aktuelle** Menü 1:1 nach,
  damit das Verhalten ohne Konfiguration unverändert bleibt (z.B. Folder:
  `open, compress.zip, compress.targz, rename, delete, permissions, newfolder,
  refresh`; Archive: `open, extract, rename, delete, permissions, newfolder,
  refresh`; Background: `newfolder, refresh`; usw.).
- Config-Key: `"contextmenu.layout"`.

### Renderer-Umbau: `components/ContextMenu.razor`

- `@inject IConfigRepository Config` ergänzen; Feld `ContextMenuConfig _config`.
- In `OnInitializedAsync`: `_config = await Config.GetAsync("contextmenu.layout",
  ContextMenuConfig.Default())`. (Lädt einmal pro Seitenaufruf; nach einer
  Admin-Änderung greift es beim nächsten Navigieren zu Files – kein Neustart
  nötig.)
- `BuildContextMenuItems()` neu:
  1. `scope = ContextMenuScope.ResolveScope(selectedItems)`
  2. Header automatisch setzen (wie bisher): Einzel → `target.Name`,
     Mehrfach → `string.Format(Resources.Context_Menu_SelectedItems, n)`.
  3. IDs aus `_config.Menus[scope]` (Fallback `Default`) iterieren; je ID
     `ContextCommandCatalog.ById(id)` holen, Laufzeit-Guards prüfen
     (`permissions`→`FileBrowser.CurrentUserIsAdmin()`; `extract` in
     MultiSelection→`selected.All(IsArchive)`), dann via `builder.Add(...)` mit
     der über `BuildAction(id, target/selected)` erzeugten Aktion einreihen.
- Neue private Methode `Func<Task> BuildAction(string id, FileMetadata? target,
  HashSet<FileMetadata> selected)` – enthält den `switch (id)` mit den **heute
  schon vorhandenen** Lambdas (Open/Preview, Unzip, ArchiveFiles, Rename,
  Delete, Acl, NewFolder, Refresh). Die bestehenden `Context*`-Hilfsmethoden
  bleiben unverändert.
- Die bisherige `addGeneralItems`-Sonderlogik entfällt – `newfolder`/`refresh`
  sind normale Katalog-Befehle und werden pro Kategorie im Layout platziert.
- Auto-Trenner: vor `newfolder`/`refresh` (bzw. zwischen „Aktions"- und
  „Allgemein"-Gruppe) optional automatisch einen Separator einfügen, um die
  heutige Optik beizubehalten. Einfachste Variante: Separator einfügen, wenn der
  vorige Befehl kein General-Befehl war und der aktuelle einer ist.

`FileBrowser.razor` braucht keine Änderung (Aufrufe von `OnItemContextMenu`
bleiben gleich; Config wird in `OnInitializedAsync` der Komponente geladen).

### Settings-GUI: `Settings.razor` + `SettingsViewModel.cs`

**`SettingsViewModel.cs`:**
- Neue State-Felder: `ContextMenuConfig CtxConfig`, `ContextMenuScope
  SelectedScope`, abgeleitete Listen `AssignedCommands` (geordnet) und
  `AvailableCommands` (Katalog für Scope minus zugewiesene).
- `LoadAsync`: bei `CanManageSettings` zusätzlich
  `CtxConfig = await _config.GetAsync("contextmenu.layout", Default())`.
- Methoden: `AddCommand(id)`, `RemoveCommand(id)`, `MoveUp(id)`,
  `MoveDown(id)`, `SelectScope(scope)`, `ResetScopeToDefault()`,
  `SaveContextMenuAsync()` → `_config.SetAsync("contextmenu.layout", CtxConfig)`
  + Erfolg/Fehler-Message (analog `SaveLanguageAsync`).

**`Settings.razor`:**
- Neuer Tab `ContextMenu` neben Sprache/Datendienste (sichtbar bei
  `VM.CanManageSettings`), Enum `SettingsTab.ContextMenu`.
- Tab-Inhalt:
  - Kategorie-Auswahl (Dropdown/Liste der 9 Scopes mit lokalisierten Namen).
  - Zwei-Spalten-Transferliste: **Verfügbar** (`VM.AvailableCommands`) ↔
    **Im Menü** (`VM.AssignedCommands`), mit ►/◄ und ▲/▼ Buttons; Icons via
    `Assets.SVG(cmd.Icon, ...)`, Label via `cmd.Label()`.
  - Buttons **Speichern** (`SaveContextMenu`) und **Zurücksetzen**
    (`ResetScopeToDefault`).
- Wiederverwendung vorhandener CSS-Klassen (`detail-section`, `detail-tab`,
  `btn-primary`, `btn-icon`, `edit-input`) – möglichst keine neuen Styles, ggf.
  kleine Ergänzung in `Settings.razor.css` für die Transferliste.

### Lokalisierung (3 Dateien, siehe resx-Memo)

Neue Keys in `Resources.resx`, `Resources.de.resx`, `Resources.Designer.cs`
(Befehls-Labels `Context_Menu_*` existieren bereits und werden wiederverwendet):
- Tab/Sektion: `Web_Settings_ContextMenu`, `Web_Settings_ContextMenu_Desc`
- Spalten/Buttons: `Web_Settings_CtxAvailable`, `Web_Settings_CtxAssigned`,
  `Web_Settings_CtxReset`, `Web_Settings_CtxSaved`, `Web_Settings_CtxSaveFailed`
- Kategorie-Namen: `Web_Settings_CtxScope_Folder`, `_Archive`, `_Image`,
  `_Video`, `_Audio`, `_Document`, `_OtherFile`, `_Multi`, `_Background`

## Betroffene Dateien

Neu:
- `src/Kaimo_File_Server.Web/DynamicHelpers/ContextMenuScope.cs`
- `src/Kaimo_File_Server.Web/DynamicHelpers/ContextCommand.cs`
- `src/Kaimo_File_Server.Web/DynamicHelpers/ContextMenuConfig.cs`

Geändert:
- `src/Kaimo_File_Server.Web/Components/Pages/Files/FileBrowser/components/ContextMenu.razor`
- `src/Kaimo_File_Server.Web/Components/Pages/Settings/Settings.razor`
- `src/Kaimo_File_Server.Web/Components/Pages/Settings/Settings.razor.css` (klein)
- `src/Kaimo_File_Server.Web/Components/ViewModels/SettingsViewModel.cs`
- `src/Kaimo_File_Server.Core/Language/Resources.resx`
- `src/Kaimo_File_Server.Core/Language/Resources.de.resx`
- `src/Kaimo_File_Server.Core/Language/Resources.Designer.cs`

Wiederverwendet (nicht geändert): `ContextMenuBuilder`/`ContextMenuItem`,
`FileHelper` (`IsArchive`, `GetPreviewKind`), `IConfigRepository`,
`FileBrowser`-`Context*`-Methoden.

## Verifikation

1. **Build**: `dotnet build` der Solution – keine Compile-Fehler.
2. **Default-Verhalten**: App starten, ohne gespeicherte Konfiguration
   Rechtsklick auf Ordner/Datei/Archiv/Mehrfachauswahl/leeren Bereich →
   Menüs müssen **identisch zum heutigen** Stand aussehen (`Default()` deckt das
   ab).
3. **Konfiguration**: Als Nutzer mit `ManageSystemSettings` in Settings → Tab
   „Kontextmenü": Kategorie *Bild* wählen, `delete` entfernen, `rename` nach
   oben schieben, speichern.
4. **Wirkung**: Zu Files navigieren, Rechtsklick auf ein Bild → geänderte
   Reihenfolge/Sichtbarkeit sichtbar; Rechtsklick auf einen Ordner →
   unverändert (kategoriespezifisch).
5. **Guards**: `permissions` erscheint weiterhin nur als Admin; `extract` in
   Mehrfachauswahl nur wenn alle Selektierten Archive sind.
6. **Reset**: „Zurücksetzen" stellt die Default-Reihenfolge der Kategorie wieder
   her.
