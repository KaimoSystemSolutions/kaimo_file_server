# Cloud Access: virtuelle Remote-Shares im Web-Dateibrowser

> **Status:** Entscheidungs- und Umsetzungsplan, noch nicht zur Implementierung freigegeben
> **Erstellt:** 2026-08-10
> **Ziel:** Remote-Speicher ohne Synchronisierung als virtuelle Shares im Web-Dateibrowser bereitstellen
> **Erste Ziel-Backends:** Microsoft OneDrive; danach optional Google Drive, SMB und NFS
> **Bewertung:** Technisch realistisch, aber ein eigenständiges Subsystem mit mittlerer bis hoher Komplexität

---

## Implementierungsstand der Browser-Vorbereitung

Am 2026-08-10 wurde die UI-seitige Architekturvorbereitung umgesetzt:

- Die bisher routbare große `FileBrowser`-Komponente ist jetzt eine wiederverwendbare UI-Komponente.
- `LocalFileBrowserPage.razor` hostet diese Komponente für die bestehenden `/files`-Routen.
- `IFileBrowserViewModel` entkoppelt die UI vom konkreten lokalen `FileBrowserViewModel`.
- `BrowserCapabilities` steuert lokale beziehungsweise remote verfügbare Aktionen.
- Vorschau-, Eigenschaften- und Versionsdialog erhalten das aktive Browser-ViewModel als Parameter,
  statt selbst immer das lokale ViewModel zu injizieren.
- `BrowserShareInfo` erlaubt Remote-Shares ohne gefälschtes lokales `ShareDefinition.Path`.
- `RemoteFileBrowserViewModelBase` stellt Browserzustand und Navigation als Einstiegspunkt für
  spätere OneDrive-, SMB- oder NFS-ViewModels bereit.
- `OverviewRoute` und `ShareRoute` machen Breadcrumbs und Navigation für Remote-Routen nutzbar.

Noch nicht implementiert sind Remote-Persistenz, Grants, Credential-Schutz, Provideroperationen,
Cloud-Access-Verwaltungsseite und Cross-Backend-Transfers. Der bestehende lokale Dateibrowser
verwendet weiterhin seine bisherige `IFileService`-Logik.

---

## 1. Kurzfazit und Empfehlung

Die Idee ist technisch gut umsetzbar und passt grundsätzlich zur vorhandenen Trennung zwischen
Dateibrowser, `IFileService` und Cloud-Providern. Sie sollte jedoch **nicht** als weiterer Modus von
Cloud Sync und auch **nicht** als normales lokales `ShareDefinition` umgesetzt werden.

Empfohlen wird ein eigener Entitätstyp **Cloud-Access-Share** beziehungsweise **virtuelles
Web-Share**:

- Im Dateibrowser sieht es für Benutzer wie ein normales Share aus.
- Verzeichnisauflistungen laden nur Remote-Metadaten wie Name, Typ, Größe und Änderungsdatum.
- Dateiinhalt wird erst bei Vorschau, Download oder Kopieren gelesen.
- Erstellen, Umbenennen, Verschieben und Löschen werden direkt beim Remote-Backend ausgeführt.
- Es gibt keine Hintergrund-Synchronisierung und keine vollständige lokale Kopie.
- Das Share wird ausschließlich im Web angezeigt und niemals an Samba weitergegeben.
- Die Berechtigung gilt nur für das gesamte virtuelle Share: Zugriff oder kein Zugriff. Innerhalb
  des Shares werden im MVP keine Kaimo-Datei-ACLs ausgewertet.

### Gesamtbewertung

| Bereich | Einschätzung |
|---|---|
| OneDrive-MVP | **Gut realisierbar**; vorhandene OAuth-, Listing-, Upload- und Download-Logik kann teilweise wiederverwendet werden |
| Google Drive | **Realisierbar**, aber Google-Workspace-Dateien, Pagination und besondere Drive-Semantik brauchen Zusatzarbeit |
| SMB als Remote-Quelle | **Realisierbar**, aber Betriebsmodell und Client-/Mount-Strategie müssen zuerst per Spike entschieden werden |
| NFS als Remote-Quelle | **Realisierbar mit Einschränkungen**; Authentisierung, UID/GID und privilegierte Mounts sind betrieblich anspruchsvoll |
| Einheitlicher Dateibrowser | **Realisierbar**, erfordert aber eine neue browserorientierte Abstraktion statt direkter Nutzung von `IFileService` |
| Cloud → lokales Share kopieren | **Gut realisierbar** als gestreamter, abbrechbarer Transferjob |
| Aufwand | OneDrive-Produktions-MVP ungefähr **5–8 Entwicklerwochen**, danach je Backend zusätzlicher Aufwand |

**Empfehlung:** Einen begrenzten OneDrive-Spike bauen und erst nach dessen Erfolg das vollständige
MVP freigeben. SMB und NFS sollen nicht Teil des ersten MVP sein.

---

## 2. Gewünschtes Verhalten

### 2.1 Aus Sicht des Administrators

Unter einem neuen Navigationspunkt **Cloud Access** kann ein Administrator:

1. eine Remote-Verbindung anlegen oder auswählen;
2. einen Provider auswählen, zum Beispiel OneDrive, Google Drive, SMB oder NFS;
3. einen Remote-Ordner auswählen, der als Wurzel des virtuellen Shares dient;
4. einen sichtbaren Namen und eine Beschreibung vergeben;
5. das Share aktivieren oder deaktivieren;
6. Benutzer und Gruppen zuweisen, die das gesamte Share benutzen dürfen;
7. optional das gesamte Share auf schreibgeschützt stellen;
8. die Verbindung testen und den letzten Fehler beziehungsweise Status sehen.

Eine Remote-Verbindung soll wiederverwendbar sein. So kann beispielsweise ein OneDrive-Konto
mehrere unterschiedliche Remote-Ordner als getrennte virtuelle Shares bereitstellen, ohne das
OAuth-Konto mehrfach zu verbinden.

### 2.2 Aus Sicht des Benutzers

Ein berechtigter Benutzer sieht das virtuelle Share zusammen mit den lokalen Shares in der
Share-Übersicht. Es erhält dabei ein Provider-Symbol und eine Kennzeichnung wie „Remote“.

Folgende Semantik gilt:

| Benutzeraktion | Remote-Verhalten |
|---|---|
| Share oder Ordner öffnen | Nur direkte Kinder als Metadaten laden |
| Datei öffnen / Vorschau | Inhalt jetzt vom Provider laden; dies zählt bewusst als echter Zugriff |
| Download | Provider-Inhalt direkt durch den Webserver zum Browser streamen |
| Upload | Direkt zum Remote-Provider streamen |
| Ordner erstellen | Remote-Ordner erstellen |
| Umbenennen | Remote-Element umbenennen |
| Innerhalb desselben virtuellen Shares verschieben | Providerseitige Move-/Rename-Operation verwenden |
| Innerhalb desselben Backends kopieren | Providerseitige Copy-Operation verwenden, wenn unterstützt |
| Cloud Access → lokales Share kopieren | Vom Provider lesen und direkt in `IFileService` des Ziel-Shares schreiben |
| Lokales Share → Cloud Access kopieren | Sinnvolle Folgestufe; gleicher Transferweg in Gegenrichtung |
| Zwischen zwei Remote-Backends kopieren | Über den Kaimo-Server streamen, sofern kein gemeinsamer Provider-Kontext existiert |
| Löschen | Remote löschen oder in den Provider-Papierkorb verschieben, abhängig von Provider und Konfiguration |

### 2.3 Bewusste Nicht-Ziele des MVP

- keine Offline-Nutzung;
- keine automatische oder geplante Synchronisierung;
- kein Export über Kaimos Samba-Server und kein Zugriff über `\\server\share`;
- keine Kaimo-Datei-ACLs innerhalb des Remote-Verzeichnisbaums;
- keine lokale Versionierung von Remote-Dateien;
- kein lokaler Papierkorb für Remote-Dateien;
- keine Volltextindizierung oder globale Suche über Remote-Inhalte;
- keine rekursive Größenberechnung beim Öffnen einer Seite;
- kein dauerhaftes Content-Caching;
- keine atomaren Verschiebeoperationen zwischen unterschiedlichen Backends.

Diese Grenzen sind wichtig: Ohne sie würde aus „Remote-Zugriff“ faktisch wieder ein Sync-,
Index- oder Cache-System werden.

---

## 3. Relevanter Ist-Zustand im Repository

### 3.1 Was bereits gut vorbereitet ist

- `ICloudConnection` bietet heute bereits Listing, Download, Upload und Ordnererstellung.
- `OneDriveConnection` implementiert Remote-Listing, Streaming-Download, direkten Upload und
  große Upload-Sessions über Microsoft Graph.
- `GoogleDriveConnection` bietet entsprechende Grundoperationen über die Google Drive API.
- `ICloudProvider` und `CloudProviderFactory` bilden bereits eine Provider-Registry und einen
  Connection-Cache.
- Der Dateibrowser greift zentral über `FileBrowserViewModel` und `IFileServiceFactory` zu.
- Die Share-Übersicht prüft bereits Root-Zugriff, bevor ein lokales Share angezeigt wird.
- Der aktuelle Datei-Upload ist abbrechbar und besitzt Job-/Fortschrittslogik, die als Muster
  für Remote-Transfers dienen kann.

### 3.2 Warum die vorhandenen Typen nicht direkt genügen

`ShareDefinition` beschreibt aktuell ausdrücklich ein lokales, physisches Verzeichnis:

- `Path` ist verpflichtend und muss auf dem Host erreichbar sein;
- `ShareRepository.GetAllEnabledAsync()` filtert nach lokal erreichbaren Volumes;
- `FileServiceFactory` erzeugt immer ein `FileSystemStorage`;
- der Samba-Bridge-Service veröffentlicht alle aktivierten lokalen Shares;
- lokale Versionierung, Ownership, ACL-Metadaten, Suche und Lifecycle-Hooks setzen ein lokales
  Dateisystem voraus.

Ein Cloud-Access-Share in dieselbe Entität zu pressen würde an vielen Stellen Sonderfälle
erzeugen und birgt das Risiko, ein Remote-Share versehentlich an Samba zu veröffentlichen.

Auch `ICloudConnection` ist noch keine vollständige Remote-Dateisystem-Schnittstelle. Es fehlen
unter anderem:

- einzelne Metadatenabfrage beziehungsweise stabile Remote-ID;
- Löschen;
- Umbenennen und Verschieben;
- serverseitiges Kopieren;
- Capability-Angaben pro Provider und gegebenenfalls pro Datei;
- Konflikt- und Überschreibungssemantik;
- ETag-/Versions-Prüfung für konkurrierende Änderungen;
- optionaler Range-Read für große Vorschauen oder fortgesetzte Downloads.

Zusätzlich ist der aktuelle Connection-Lifecycle noch auf Sync-Mappings zugeschnitten:
`CloudProviderFactory` adressiert Verbindungen über Share und Credential-Fingerprint. Für
wiederverwendbare Remote-Verbindungen muss stattdessen die neue Connection-ID die stabile
Identität bilden. Besonders wichtig: `GoogleDriveConnection.Dispose()` widerruft aktuell den
Refresh-Token. Ein normales Cache-Evict oder das Schließen einer interaktiven Session darf bei
Cloud Access niemals automatisch die gesamte Provider-Autorisierung widerrufen.

`IFileService` ist umgekehrt zu groß und lokal geprägt. Methoden wie `ToAbsolutePath`, Snapshots,
externe Samba-Close-Hooks, Unzip und lokales Archivieren ergeben für ein virtuelles Remote-Share
nicht überall Sinn.

### 3.3 Bestehende Stellen, die beim MVP geändert werden müssen

| Bereich | Heute | Benötigte Änderung |
|---|---|---|
| Share-Übersicht | Nur `ShareDefinition` | Lokale und virtuelle Shares zu einem Browser-Modell zusammenführen |
| Share-Routing | `/files/{shareName}` | Für Remote zunächst eindeutiges `/files/access/{id}` verwenden |
| Filebrowser-VM | Hält genau ein `IFileService` | Browser-Backend anhand des Share-Typs auflösen |
| Copy/Paste | Nur aktueller Share-Kontext; Pfade ohne Quellidentität | Clipboard-Eintrag muss Backend-/Share-ID und Pfad enthalten |
| Größenanzeige | Berechnet Share- und Ordnergrößen rekursiv | Für Remote deaktivieren oder nur bereits gelieferte Dateigröße anzeigen |
| ACL-UI | ACLs für Pfade und Dateien | Bei Cloud Access nur Share-Zuweisungen anzeigen |
| Versionen / Suche | Für lokale Dateien verfügbar | Für Remote im MVP ausblenden |
| Samba Share-Sync | Alle lokalen aktivierten Shares | Unverändert lassen; Cloud-Access-Entitäten nie ausliefern |

---

## 4. Zielarchitektur

```text
                         Web-Dateibrowser
                                |
                                v
                    IBrowserShareResolver
                       /                 \
                      v                   v
          LocalBrowserBackend      RemoteBrowserBackend
                  |                        |
             IFileService          IRemoteFileSession
                  |                        |
          FileSystemStorage        Provider-Adapter
                              /        |        |        \
                         OneDrive  Google    SMB       NFS
                           API      API    Client/Mount Mount

 CloudAccessShare ---- RemoteConnection ---- verschlüsselte Credentials
         |
         `---- CloudAccessGrant (User/Gruppe: Zugriff auf das ganze Share)
```

Die UI soll nur das kleine, gemeinsame Browser-Feature-Set sehen. Lokale Spezialfunktionen
bleiben im lokalen Adapter; Remote-Funktionen werden durch Capabilities gesteuert.

### 4.1 Empfohlene neue Abstraktionen

```csharp
public interface IBrowserShareBackend
{
    BrowserBackendCapabilities Capabilities { get; }
    Task<IReadOnlyList<BrowserItem>> ListAsync(string path, UserContext user, CancellationToken ct);
    Task<BrowserItem> GetMetadataAsync(string path, UserContext user, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, UserContext user, CancellationToken ct);
    Task WriteAsync(string path, Stream content, WriteOptions options, UserContext user, CancellationToken ct);
    Task CreateDirectoryAsync(string path, UserContext user, CancellationToken ct);
    Task DeleteAsync(string path, UserContext user, CancellationToken ct);
    Task MoveAsync(string source, string destination, UserContext user, CancellationToken ct);
    Task CopyAsync(string source, string destination, UserContext user, CancellationToken ct);
}
```

Der genaue Vertrag kann bei der Implementierung anders benannt werden. Entscheidend ist:

1. Der Dateibrowser hängt nicht mehr direkt von lokalen Pfaden ab.
2. Jede Operation erhält `CancellationToken`.
3. Capabilities entscheiden, welche UI-Aktionen angeboten werden.
4. Autorisierung wird serverseitig bei **jeder** Operation erneut geprüft.
5. Remote-Pfade werden immer relativ zur festgelegten Remote-Wurzel normalisiert.

### 4.2 Provider-Contract

Für Remote-Provider wird ein kleinerer, speicherspezifischer Vertrag empfohlen:

- `IRemoteFileProvider`: beschreibt Provider, Konfigurationsschema, Authentisierung und
  Capability-Grundmenge;
- `IRemoteFileSession`: authentisierte Verbindung für Listing und Dateioperationen;
- `IRemoteConnectionFactory`: Connection-Pooling, Token-Rotation und sichere Freigabe;
- `RemoteItem`: stabile Provider-ID, Name, relativer Pfad, Typ, Größe, Zeit, ETag und individuelle
  Capabilities.

Die bestehenden OneDrive-/Google-Klassen können intern wiederverwendet oder schrittweise auf
diesen Vertrag umgestellt werden. `ICloudConnection.SyncAsync()` soll nicht zum Kern der neuen
Abstraktion werden, da Synchronisierung und interaktiver Dateizugriff unterschiedliche
Fehler-, Cache- und Nebenwirkungsmodelle haben.

---

## 5. Persistenzmodell

### 5.1 `remote_connections`

Eine Verbindung repräsentiert ein authentisiertes Remote-Konto oder Protokollziel.

| Feld | Zweck |
|---|---|
| `id` | Stabile interne ID |
| `provider_id` | `onedrive`, `google-drive`, `smb`, `nfs`, später weitere |
| `display_name` | Administrativer Anzeigename |
| `encrypted_credentials` | Providerdaten ausschließlich verschlüsselt |
| `settings` | Nicht geheime, validierte Provider-Konfiguration |
| `credential_version` | Rotation/Migration des Verschlüsselungsformats |
| `created_at_utc`, `updated_at_utc` | Audit |
| `last_test_at_utc`, `last_test_status` | Zustandsanzeige ohne Geheimnisse |

### 5.2 `cloud_access_shares`

| Feld | Zweck |
|---|---|
| `id` | Share-ID |
| `connection_id` | Zugehörige Remote-Verbindung |
| `name` | Sichtbarer Name |
| `description` | Beschreibung |
| `remote_root_id` | Stabile Provider-ID des ausgewählten Wurzelordners, falls vorhanden |
| `remote_root_path` | Lesbarer beziehungsweise protokollbasierter Pfad |
| `department_id` | Verwaltungs-Scope, analog zu lokalen Shares |
| `is_enabled` | Gesamten Zugriff aktivieren/deaktivieren |
| `is_read_only` | Alle mutierenden Aktionen global sperren |
| `delete_behavior` | Provider-Papierkorb oder permanent, soweit steuerbar |
| `created_at_utc`, `updated_at_utc` | Audit |

`remote_root_id` ist für API-Provider vorzuziehen. Ein nur nach Namen gespeicherter Pfad kann
ungültig werden, wenn ein Elternordner außerhalb von Kaimo umbenannt wird.

### 5.3 `cloud_access_grants`

| Feld | Zweck |
|---|---|
| `cloud_access_share_id` | Virtuelles Share |
| `principal_id` | Kaimo-Benutzer oder -Gruppe |
| `created_by`, `created_at_utc` | Audit |

Ein Eintrag bedeutet im MVP: Der Principal darf das gesamte Share im Rahmen der globalen
Read-only-Einstellung und der Provider-Capabilities benutzen. Kein Eintrag bedeutet kein Zugriff.

Empfohlene Regeln:

- Default-Deny;
- Benutzer- und Gruppenzuweisungen;
- keine Vererbung in Unterordner;
- keine Vermischung mit externen OneDrive-/SMB-ACLs in der Kaimo-UI;
- Providerberechtigungen bleiben eine zusätzliche harte Grenze;
- ein deaktiviertes Share verweigert auch direkte URLs;
- Administration über ein neues `CloudAccessAdmin`-Managementrecht, nach Department-Scope
  auflösbar.

---

## 6. Sicherheitsmodell

### 6.1 Credential-Schutz ist eine P0-Voraussetzung

Die aktuellen Cloud-Sync-Refresh-Tokens werden in `CloudSettings` als JSON-Felder persistiert;
im untersuchten Code ist dafür keine Verschlüsselungsschicht sichtbar. Für Cloud Access dürfen
OAuth-Refresh-Tokens, SMB-Passwörter, NFS-Kerberos-Material oder Zugangsschlüssel **nicht** als
Klartext in der Datenbank landen.

Vor dem produktiven MVP wird benötigt:

- ein `IRemoteCredentialProtector` mit authentifizierter Verschlüsselung;
- ein außerhalb der Datenbank liegender Schlüssel;
- fail-closed Startverhalten bei fehlendem Schlüssel;
- Schlüsselversionierung und Rotationspfad;
- niemals Secrets in Logs, Exceptions, Audit-Payloads oder UI-Modelle übernehmen;
- optional spätere Migration der bereits vorhandenen Cloud-Sync-Credentials in dasselbe sichere
  Connection-Modell.

Die vorhandene Data-Protection-Key-Persistenz und der vorhandene AES-GCM-Ansatz für NT-Hashes
sind brauchbare Muster, sollten aber in einer Infrastrukturkomponente nutzbar sein und nicht nur
im Web-Prozess leben.

### 6.2 Zugriff und Identität

Der Remote-Provider sieht in der Regel **eine technische beziehungsweise verbundene Identität**,
nicht den einzelnen Kaimo-Benutzer. Kaimo muss deshalb jeden Zugriff selbst prüfen und auditieren.

Mindestens zu protokollieren:

- Kaimo-Benutzer-ID;
- Cloud-Access-Share-ID und Provider;
- Operation;
- normalisierter relativer Pfad beziehungsweise gehashter Pfad nach Logging-Policy;
- Ergebnis, Dauer und transferierte Bytes;
- Provider-Korrelations-ID, soweit verfügbar;
- keine Tokens, Passwörter oder vorautorisierten Download-URLs.

### 6.3 Pfad- und Netzwerkabsicherung

- `..`, absolute Pfade, alternative Separatoren und codierte Traversals abweisen;
- die ausgewählte Remote-Wurzel nach jeder Pfadkombination erzwingen;
- bei API-Providern möglichst IDs statt ungeprüfter URL-Eingaben verwenden;
- SMB-/NFS-Ziele gegen eine definierte Netzwerkpolicy validieren, damit die Funktion nicht als
  SSRF- oder internes Portscan-Werkzeug missbraucht werden kann;
- SMB-Dialekt, Signierung und möglichst Verschlüsselung konfigurierbar und sicher voreinstellen;
- Symlinks/Reparse-Points bei mountbasierten Backends dürfen die Wurzel nicht verlassen;
- kurzlebige Provider-Download-URLs nie an unberechtigte Clients oder Logs weiterreichen.

---

## 7. Lazy Loading, Streaming und Cache-Regeln

### 7.1 Listing

`ListAsync` lädt ausschließlich die direkten Kinder des aktuellen Ordners. Keine rekursive
Vorab-Abfrage, keine Größenberechnung für Ordner und kein automatisches Preloading des nächsten
Levels.

Eine kurze Metadaten-Cachezeit von beispielsweise 15–30 Sekunden ist sinnvoll, um wiederholte
Renders und Doppelklicks abzufangen. Jede erfolgreiche Mutation invalidiert den betroffenen
Elternordner. Ein manueller Refresh umgeht den Cache.

### 7.2 Dateiinhalt

Dateiinhalte sollen grundsätzlich gestreamt werden:

```text
Remote Provider -> Kaimo Web -> Browser oder lokales IFileService
```

Der Server soll nicht erst die gesamte Datei in `MemoryStream` laden. Nur Funktionen, die einen
seekbaren Stream zwingend benötigen, dürfen eine begrenzte temporäre Datei verwenden. Dafür sind
Größenlimit, freier Speicher, Abbruchbereinigung und Dateiberechtigungen festzulegen.

### 7.3 Downloads und Vorschau

- Download über einen autorisierten Server-Endpunkt mit Response-Streaming und Abbruch bei
  Client-Disconnect;
- Range-Requests nach Möglichkeit durchreichen oder implementieren;
- Vorschaugrenze beibehalten; eine Vorschau lädt realen Inhalt und ist kein Metadatenzugriff;
- Providerfehler wie `401`, `403`, `404`, `409`, `412` und Rate-Limits in verständliche
  Dateibrowserfehler übersetzen.

---

## 8. Dateioperationen und Konsistenz

### 8.1 Capability-Modell

Nicht jeder Provider und nicht jedes Element unterstützt alle Operationen. Die UI darf das nicht
erraten. Sinnvolle Flags sind:

- List, Read, Write, CreateDirectory;
- Delete beziehungsweise Trash;
- Rename, MoveWithinBackend;
- CopyWithinBackend;
- RangeRead;
- ReplaceExisting;
- StableItemIds;
- NativeOfficeDocumentExport.

Globale Read-only-Konfiguration und Provider-Capabilities werden geschnitten. Eine Aktion ist nur
sichtbar, wenn beide sie zulassen.

### 8.2 Gleichzeitige externe Änderungen

Remote-Inhalte können außerhalb von Kaimo verändert werden. Wo verfügbar, werden ETag oder
Versions-ID beim Listing aufgenommen und für mutierende Aktionen per If-Match verwendet.

- Bei Konflikt nicht still überschreiben.
- UI meldet „Datei wurde zwischenzeitlich geändert“ und bietet Aktualisieren an.
- Namenskollisionen müssen eine explizite Policy haben: Fehler, Ersetzen oder automatisch
  Umbenennen. Für den MVP wird **Fehler** als sichere Voreinstellung empfohlen.

### 8.3 Löschen

„Löschen“ ist zwischen Providern nicht einheitlich. Das Backend muss zurückmelden, ob es in einen
Provider-Papierkorb verschiebt oder permanent löscht. Ein lokaler `.recycle`-Ordner darf für
virtuelle Shares nicht verwendet werden.

### 8.4 Verschieben und Kopieren

- Move innerhalb desselben Remote-Backends: native Provideroperation, schnell und ohne
  Content-Transfer.
- Copy innerhalb desselben Backends: native Copy, falls unterstützt; sonst Stream-Fallback.
- Copy Remote → lokal: Stream-Transfer mit temporärem Ziel und atomarer Veröffentlichung durch
  die vorhandene lokale Write-Semantik.
- Move zwischen Backends: zunächst nicht anbieten. Später als Copy + verifiziertes Delete, klar
  als nicht atomar gekennzeichnet.

---

## 9. Cross-Share-Transfer

Die aktuelle Zwischenablage speichert nur `FileMetadata` und den aktuellen Pfad. Das reicht nicht
für Wechsel zwischen Shares. Benötigt wird ein langlebiger, serverseitiger Transfer-Descriptor:

```text
SourceShareKind + SourceShareId + SourcePath + ItemType + optional ETag
```

Ein neuer `IFileTransferService` koordiniert Quelle und Ziel:

1. Quell- und Zielberechtigung unmittelbar vor Start prüfen;
2. Zielnamen und Konfliktpolicy validieren;
3. Ordner bei Bedarf lazy rekursiv enumerieren;
4. Daten mit begrenztem Puffer streamen;
5. Fortschritt, Rate und aktuelle Datei an `JobService` melden;
6. Cancellation bis zu beiden Backends weiterreichen;
7. unvollständige Zieldatei entfernen oder niemals sichtbar veröffentlichen;
8. Ergebnis je Datei protokollieren;
9. bei einem späteren Cut erst nach verifiziertem Copy-Erfolg löschen.

Für das erste MVP genügt **Cloud Access → lokales Share kopieren**. Upload sowie lokale
Dateibearbeitung im Remote-Share können bereits separat funktionieren; ein voll symmetrischer
Clipboard-Workflow kann danach folgen.

---

## 10. Provider-Bewertung

### 10.1 Microsoft OneDrive – empfohlen für MVP

Die vorhandene Implementierung deckt einen großen Teil des Datenpfads ab. Microsoft Graph bietet
für `driveItem` Listing, Content-Download, Upload, Delete, Move und Copy. Move innerhalb eines
Drives erfolgt als Metadatenänderung; Copy kann asynchron sein und braucht deshalb einen
Operationsstatus.

Benötigte Ergänzungen:

- stabile Item-IDs, ETag und Capabilities in `RemoteItem` übernehmen;
- Delete/Trash, Rename, Move und Copy implementieren;
- Copy-Status polling mit Timeout und Cancellation;
- Remote-Wurzel anhand Item-ID adressieren;
- große Downloads streamen und Client-Abbruch durchreichen;
- Rate-Limit- und Retry-After-Behandlung;
- OAuth-Flow nach Zweck „Cloud Access“ entkoppeln;
- Refresh-Token verschlüsselt persistieren und Rotation atomar speichern.

**Machbarkeit:** hoch.

### 10.2 Google Drive

Listing, Download, Upload und Ordnererstellung existieren bereits. Google unterstützt Metadaten-
Updates zum Verschieben/Umbenennen, Delete und serverseitiges Kopieren von Dateien. Ordner können
nicht mit einem einzigen Copy-Aufruf rekursiv kopiert werden.

Zusätzliche Punkte:

- stabile Datei-IDs statt alleiniger Pfadauflösung verwenden;
- Pagination vollständig behandeln;
- My Drive und Shared Drives korrekt unterscheiden;
- Google-native Docs/Sheets/Slides sind keine normalen Binärdateien und benötigen eine
  definierte Exportstrategie;
- Shortcuts, mehrere Sondertypen, Trash und Download-/Copy-Restriktionen behandeln;
- externe Änderungen und Namensgleichheit berücksichtigen.

**Machbarkeit:** mittel bis hoch; nach OneDrive sinnvoll.

### 10.3 SMB als Remote-Backend

Das vorhandene Samba-System ist ein **Server** für lokale Kaimo-Shares und kann nicht einfach als
Remote-SMB-Client wiederverwendet werden. Es gibt zwei realistische Betriebsmodelle:

#### Variante A – dedizierter SMB-Client-Adapter

- .NET oder Sidecar spricht SMB direkt;
- gute Kontrolle über Credentials, Timeouts und Operationen;
- kein privilegierter Kernel-Mount;
- Bibliotheksreife, Dialekte, Signing, Encryption, Locks und große Dateien müssen intensiv
  geprüft werden.

#### Variante B – CIFS-Mount außerhalb des Webprozesses

- Betriebssystem/Samba-Client übernimmt Protokolldetails;
- Dateioperationen wirken fast wie lokales Dateisystem und bleiben durch den Kernel lazy;
- benötigt sichere Mount-Verwaltung, Netzwerkfreigaben und je nach Umgebung zusätzliche
  Privilegien;
- ein Mount-Hänger darf nicht den gesamten Webprozess blockieren;
- Mountpfad darf ausschließlich im Web-/Remote-Backend verfügbar sein und nicht im Samba-
  Container erscheinen.

**Empfehlung:** Vor einer Entscheidung ein isolierter 3–5-Tage-Spike mit den echten
Deployment-Zielen. Für ein Appliance-/Docker-Produkt ist ein kleiner, stark eingeschränkter
Mount-Helper beziehungsweise Sidecar realistischer als `mount` aus dem Webprozess. Alternativ
werden nur vom Betreiber vorab gemountete Ziele unterstützt.

**Machbarkeit:** mittel; technisch ja, betrieblich deutlich komplexer als OneDrive.

### 10.4 NFS als Remote-Backend

NFS wird in der Praxis am zuverlässigsten als OS-Mount angebunden. Dadurch bleibt der eigentliche
Dateizugriff lazy, aber es entstehen andere Probleme:

- UID/GID-Zuordnung und Root-Squash;
- NFSv4-/Kerberos-Konfiguration;
- Mount- und Reconnect-Verhalten;
- potenziell blockierende I/O bei Serverausfall;
- Symlinks und Exportgrenzen;
- privilegierter Mount-Helper oder externe Vorab-Konfiguration.

Da Kaimo die Remote-Verbindung als technische Identität nutzt, ersetzt die Kaimo-Zuweisung keine
NFS-Serverberechtigung. Beide Ebenen müssen Zugriff erlauben.

**Machbarkeit:** mittel, aber erst nach SMB und nur mit klar definiertem Betriebsmodell.

### 10.5 Weitere Provider

Weitere Backends sollten erst nach Stabilisierung des Provider-Contracts hinzukommen. Gute
Kandidaten sind S3-kompatibler Object Storage und WebDAV. Object Storage benötigt jedoch eine
eigene Ordner-/Rename-Semantik, da „Ordner“ oft nur Präfixe und Rename häufig Copy + Delete sind.

---

## 11. UI-Plan

### 11.1 Neuer Reiter „Cloud Access“

Der Navigationspunkt ist nur für Benutzer mit `CloudAccessAdmin` sichtbar. Die Seite enthält:

- Liste virtueller Shares mit Status, Provider, Remote-Wurzel und Anzahl Zuweisungen;
- „Verbindung hinzufügen“ mit providerabhängigem Formular;
- „Remote-Ordner auswählen“ nach erfolgreicher Verbindung;
- Verbindungstest;
- Benutzer-/Gruppenzuweisung;
- Read-only-Schalter;
- Aktivieren/Deaktivieren;
- Entfernen mit deutlichem Hinweis: Nur die Kaimo-Verknüpfung wird entfernt, Remote-Dateien
  bleiben erhalten;
- optional „Autorisierung widerrufen“ als getrennte, weitreichendere Aktion.

### 11.2 Dateibrowser

- Lokale und Remote-Shares in derselben Übersicht, aber visuell unterscheidbar.
- Remote-Shares zeigen keine rekursiv berechnete Gesamtgröße.
- Bei Provider-Ausfall bleibt das Share sichtbar, zeigt beim Öffnen aber einen klaren
  Verbindungsfehler mit Retry.
- ACL-, Versionierungs-, Sync- und lokale Papierkorb-Aktionen für Remote ausblenden.
- Providerbedingt nicht unterstützte Aktionen ausblenden oder deaktivieren.
- Für Cross-Share-Copy einen Ziel-Share-/Zielordner-Dialog vorsehen; dies ist zuverlässiger als
  die aktuelle nur komponentenlokale Zwischenablage.

---

## 12. Fehler-, Retry- und Betriebsmodell

Interaktive Dateioperationen sollen nicht minutenlang unsichtbar wiederholt werden.

- Listing: kurzer Retry bei transientem Netzwerkfehler, danach sichtbarer Fehler.
- Downloads/Uploads: Job mit Abbruch; automatische Retries nur, wenn die Operation sicher
  fortsetzbar oder idempotent ist.
- Mutationen: keine blinden Retries nach unklarem Ergebnis; zuerst Remote-Zustand prüfen.
- Copy mit asynchronem Providerjob: Status persistent oder zumindest jobgebunden verfolgen.
- Circuit Breaker pro Remote-Verbindung, damit ein totes SMB-/NFS-Ziel nicht jede UI-Anfrage
  lange blockiert.
- Timeouts getrennt für Connect, Listing und Content-Transfer.
- Health-Status darf keine Verzeichnisinhalte oder Secrets verraten.

Wichtige Metriken:

- Listing-Latenz und Fehlerquote je Provider;
- offene und fehlgeschlagene Transfers;
- transferierte Bytes;
- Rate-Limits und Auth-Fehler;
- Connection-/Mount-Status;
- Cache-Hit-Rate für Metadaten;
- abgebrochene oder unvollständige Uploads.

---

## 13. Umsetzung in Phasen

### Phase 0 – Entscheidungs-Spike OneDrive (2–4 Tage)

Ziel ist ein wegwerfbarer oder minimal produktionsnaher Prototyp, noch ohne vollständige UI.

- eine bestehende OneDrive-Verbindung sicher laden;
- einen Remote-Root per ID auswählen;
- genau eine Verzeichnisebene listen;
- eine große Datei ohne Vollpufferung zum HTTP-Client streamen;
- Remote-Rename und Remote-Move ausführen;
- Remote-Datei gestreamt in ein lokales Share kopieren;
- Abbruch und 401/403/404/409/429 demonstrieren;
- nachweisen, dass das virtuelle Share nicht in `ShareGrpcService.ListShares` auftaucht.

**Go-Kriterium:** Alle Datenoperationen funktionieren ohne vollständigen Download in den RAM,
Autorisierung wird pro Request geprüft und kein Secret liegt im Klartext in DB oder Log.

### Phase 1 – Domain, Persistenz und Sicherheit (5–8 Tage)

- Migrationen für Connections, virtuelle Shares und Grants;
- Credential-Protector, Rotation und Tests;
- Repositories und Management-Permissions;
- Remote-Pfadnormalisierung und Root-Containment;
- Audit-Grundlage;
- Provider-Capability-Modell.

### Phase 2 – OneDrive-Backend (6–10 Tage)

- neuer Remote-Provider-Contract;
- OneDrive-Adapter mit List/Stat/Read/Write/Mkdir/Delete/Move/Copy;
- Item-ID, ETag, Pagination, Rate-Limits und Token-Rotation;
- Metadaten-Cache mit Invalidierung;
- Integrations- und Fehlerfalltests gegen einen Test-Drive.

### Phase 3 – Cloud-Access-Verwaltung (5–8 Tage)

- neuer Navigationspunkt und ViewModel;
- Connection-Erstellung/OAuth-Zweck;
- Remote-Folder-Picker;
- Share-Konfiguration, Grants, Aktivierung, Read-only und Verbindungstest;
- Ressourcen/Übersetzungen und responsive UI.

### Phase 4 – Dateibrowser und Transfer (7–11 Tage)

- lokale/virtuelle Share-Übersicht;
- Backend-Resolver und Remote-Route;
- capability-gesteuerte Aktionen;
- Streaming-Download und Upload;
- Cloud → lokal Copy-Job mit Fortschritt, Cancellation und Cleanup;
- Remote-Funktionen aus ACL-/Version-/Sync-/Search-UI ausschließen.

### Phase 5 – Hardening und Rollout (5–8 Tage)

- Last-, Langzeit- und große-Datei-Tests;
- Race-/ETag- und externe-Änderungs-Tests;
- Audit, Metriken, Alerting und Log-Redaction;
- Upgrade-/Rollback-Test der Migration;
- Administrator- und Benutzer-Dokumentation;
- Feature-Flag, zunächst standardmäßig aus.

### Grobe Aufwandsschätzung

| Umfang | Aufwand bei einem erfahrenen Entwickler |
|---|---|
| OneDrive-Spike | 2–4 Tage |
| Produktionsfähiges OneDrive-MVP | ungefähr 5–8 Wochen inklusive Tests und Hardening |
| Google Drive danach | zusätzlich ungefähr 1,5–3 Wochen |
| SMB danach | zusätzlich ungefähr 2–4 Wochen plus Deployment-Arbeit; erst nach Spike belastbar |
| NFS danach | zusätzlich ungefähr 2–4 Wochen; stark vom Betriebsmodell abhängig |
| Neuer einfacher API-Provider | typischerweise 1–3 Wochen nach Stabilisierung des Contracts |

Die Schätzung ist keine Zusage. Sie setzt voraus, dass keine vollständige Remote-Suche,
Versionierung, Datei-ACLs, Office-Online-Bearbeitung oder Cross-Backend-Cut-Semantik in den MVP
rutscht.

---

## 14. Teststrategie

### 14.1 Unit- und Contract-Tests

Jeder Remote-Provider muss dieselbe Testsuite erfüllen:

- Pfadnormalisierung und Root-Escape;
- Listing nur einer Ebene;
- Stream wird bei Cancellation geschlossen;
- Konfliktpolicy;
- Capability-Auswertung;
- Read-only-Verhalten;
- Zugriff verweigert ohne Grant;
- deaktiviertes Share verweigert direkte URL;
- keine Secret-Ausgabe in serialisierten Modellen und Logs.

### 14.2 Provider-Integrationstests

- Pagination mit mehr als einer Seite;
- Dateien mit Sonderzeichen und Unicode;
- 0 Byte, große Datei und nicht seekbarer Upload-Stream;
- externe Umbenennung zwischen Listing und Open;
- Tokenablauf und Tokenrotation;
- Provider-Rate-Limit;
- Abbruch während Up-/Download;
- Move-/Copy-Konflikte;
- Delete-/Trash-Semantik;
- Netzwerkausfall vor, während und nach Mutation.

### 14.3 End-to-End

- Admin erstellt Connection, wählt Unterordner und weist Gruppe zu;
- berechtigter Nutzer sieht und öffnet Share;
- unberechtigter Nutzer sieht es nicht und erhält auch über direkte URL keinen Zugriff;
- Datei wird erst beim Öffnen/Download inhaltlich übertragen;
- Cloud-Datei wird in lokales Share kopiert und ist vollständig lesbar;
- Remote-Move überträgt keine Datei durch Kaimo;
- Cloud-Access-Share taucht nie in Samba-Share-Enumeration auf;
- Entfernen des Mappings löscht keine Remote-Daten.

### 14.4 SMB-/NFS-spezifisch

- Server nicht erreichbar, DNS langsam, TCP-Timeout;
- Credentials falsch oder abgelaufen;
- Reconnect nach Verbindungsabbruch;
- Locks und gleichzeitig geöffnete Dateien;
- Symlink-/Reparse-Point-Escape;
- UID/GID-/Permission-Verhalten bei NFS;
- Webprozess bleibt responsiv, wenn Mount oder Server hängt.

---

## 15. Hauptrisiken

| Priorität | Risiko | Gegenmaßnahme |
|---|---|---|
| P0 | Remote-Credentials unverschlüsselt in DB | Credential-Protector vor erstem produktiven Mapping |
| P0 | Pfad kann Remote-Wurzel verlassen | zentrale Normalisierung, ID-basierte Root-Auflösung, Traversaltests |
| P0 | Berechtigungsprüfung nur bei Listing, nicht bei direkter URL | Grant-Prüfung in jeder Backendoperation |
| P1 | Große Datei wird im RAM gepuffert | echtes Streaming, begrenzte Buffer, temporäre Datei nur kontrolliert |
| P1 | Mutationsretry erzeugt Doppeloperation | Idempotency/ETag, Zustand nach unklarem Fehler neu lesen |
| P1 | Remote-Ausfall blockiert Webthreads | harte Timeouts, async I/O, Circuit Breaker; Mounts isolieren |
| P1 | Virtuelles Share wird versehentlich via SMB exportiert | getrennte Entität und Repository, expliziter Regressionstest |
| P1 | Cross-Backend-Move verliert Daten | im MVP deaktivieren; später Copy + Verify + Delete |
| P1 | Provider-Quoten und Rate-Limits | Pagination, Cache, Retry-After, keine rekursiven UI-Scans |
| P2 | Remote-Änderungen machen UI-Metadaten veraltet | kurze TTL, ETag, Refresh und Konfliktmeldung |
| P2 | Google-native Dateien verhalten sich nicht wie Binärdateien | Exportformat definieren oder im ersten Google-MVP ausschließen |
| P2 | SMB/NFS benötigt hohe Containerprivilegien | isolierter Helper oder betreiberseitig vorbereitete Mounts |

---

## 16. Rollout und Rückbau

- Feature-Flag `CloudAccess:Enabled`, anfangs `false`;
- Datenbankmigration nur additive Tabellen;
- OneDrive zunächst nur für Testadministratoren freischalten;
- virtuelles Share einzeln deaktivierbar;
- Entfernen eines Shares löscht ausschließlich Mapping und Grants;
- Entfernen einer Connection nur erlauben, wenn kein Share sie verwendet;
- OAuth-Widerruf und Remote-Datenlöschung niemals als Nebeneffekt eines normalen Mappings-
  Deletes ausführen;
- bei Rollback ignoriert die alte Anwendung die neuen Tabellen; lokale Shares und Samba bleiben
  unverändert.

---

## 17. Entscheidungstore

### Gate A – Spike starten?

**Empfehlung: Ja**, wenn folgende Produktgrenzen akzeptiert werden:

- OneDrive zuerst;
- Web-only;
- Root-Zugriff statt Datei-ACLs;
- keine Remote-Suche/Versionierung;
- Cloud → lokal Copy, aber noch kein Cross-Backend-Cut;
- Secretschutz ist Bestandteil, kein optionales Hardening.

### Gate B – Nach dem Spike Produktions-MVP bauen?

Nur wenn nachgewiesen ist:

- Streaming großer Dateien ohne Vollpufferung;
- zuverlässiges Remote-Move/Rename/Delete;
- stabile Remote-Root-ID;
- sichere Tokenrotation und verschlüsselte Persistenz;
- saubere Cancellation und Fehlerabbildung;
- klare Trennung zu Samba und lokalen Lifecycle-Hooks.

### Gate C – SMB hinzufügen?

Erst nach einem eigenen Spike entscheiden:

- unterstützte Deployment-Plattformen;
- Client-Library versus isolierter Mount-Helper;
- Signierung/Verschlüsselung;
- Timeout-/Hänger-Verhalten;
- Credential- und Netzwerkpolicy.

### Gate D – NFS hinzufügen?

Nur wenn ein belastbares Appliance-/Host-Betriebsmodell für Mounts, UID/GID und gegebenenfalls
Kerberos existiert. Für eine beliebige, unprivilegierte Docker-Installation ist eine komfortable
NFS-Selbstkonfiguration nicht realistisch.

---

## 18. Empfohlene offene Entscheidungen

| Frage | Empfehlung für MVP |
|---|---|
| Eigene Entität oder `ShareDefinition` erweitern? | **Eigene Entität** |
| Wer darf zugreifen? | Explizit zugewiesene Benutzer und Gruppen |
| Read-only pro Benutzer? | Nein; nur global pro virtuellem Share |
| Provider-ACLs in Kaimo spiegeln? | Nein |
| Remote-Dateien indexieren? | Nein |
| Remote-Versionen anzeigen? | Nein |
| Löschen | Provider-Papierkorb bevorzugen und Verhalten sichtbar kennzeichnen |
| Share-Gesamtgröße anzeigen? | Nein, weil dies rekursives Scannen auslösen würde |
| Content-Cache | Kein persistenter Cache im MVP |
| Gleichnamige lokale/Remote-Shares | Technisch getrennte ID-Routen; UI warnt vor Verwechslung |
| Erstes Backend | OneDrive |
| SMB/NFS im ersten Release | Nein |

---

## 19. Definition of Done für das OneDrive-MVP

- Administrator kann eine verschlüsselt gespeicherte OneDrive-Verbindung anlegen.
- Administrator kann einen Remote-Unterordner per Picker als virtuelle Wurzel auswählen.
- Benutzer und Gruppen können dem gesamten virtuellen Share zugewiesen werden.
- Berechtigte Benutzer sehen das Share im Web-Dateibrowser; unberechtigte nie.
- Direkte URL und jede Dateioperation prüfen die Zuweisung erneut.
- Listing lädt nur die aktuelle Ebene.
- Preview/Download lädt Inhalt erst bei Aufruf und streamt ohne Vollpufferung.
- Upload, Mkdir, Rename, Move und Delete funktionieren remote.
- Cloud → lokales Share Copy ist abbrechbar, zeigt Fortschritt und hinterlässt keine sichtbare
  Teildatei.
- Read-only und Provider-Capabilities werden durch UI und Backend erzwungen.
- Tokenrotation, Rate-Limits, Pagination und externe Konflikte sind getestet.
- Keine Secrets erscheinen in DB-Klartext, Logs oder Browserpayloads.
- Virtuelle Shares werden nicht über Samba veröffentlicht.
- Keine lokale Versionierung, Suche, Datei-ACL oder Sync wird versehentlich ausgelöst.
- Entfernen des Mappings löscht keine Remote-Dateien.

---

## 20. Quellen und technische Nachweise

Die Providerbewertung stützt sich neben dem vorhandenen Repository auf die offiziellen
Schnittstellendokumentationen:

- [Microsoft Graph `driveItem` – unterstützte Dateioperationen](https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0)
- [Microsoft Graph – `driveItem` verschieben](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0)
- [Google Drive API v3 – Dateioperationen](https://developers.google.com/drive/api/reference/rest/v3)
- [Google Drive – Dateien kopieren und verwalten](https://developers.google.com/workspace/drive/api/guides/create-file)
- [Google Drive – Quoten und Limits](https://developers.google.com/workspace/drive/api/guides/limits)

---

## 21. Abschließende Bewertung

Das Vorhaben ist **sinnvoll und realistisch**, wenn es als Web-Gateway zu Remote-Speichern und
nicht als Erweiterung des lokalen Dateisystems verstanden wird. Der vorhandene OneDrive-Code
reduziert den Einstieg, löst aber nur ungefähr die Hälfte der notwendigen Produktsemantik.

Der größte technische Gewinn entsteht durch eine kleine, saubere Browser-/Remote-Abstraktion.
Das größte Projektrisiko entsteht dagegen, wenn lokale `ShareDefinition`, `IFileService`, Samba,
Cloud Sync und Remote-Zugriff mit immer mehr Sonderfällen vermischt werden.

Darum lautet die konkrete Empfehlung:

1. **OneDrive-Spike freigeben.**
2. Nach erfolgreichem Gate A/B ein **begrenztes OneDrive-Web-MVP** bauen.
3. Danach Google Drive ergänzen.
4. SMB separat technisch und betrieblich evaluieren.
5. NFS nur bei klar kontrollierter Deployment-Umgebung aufnehmen.
