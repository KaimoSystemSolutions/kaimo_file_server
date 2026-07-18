# SMB-Umstieg: Samba + eigenes VFS-Modul (gRPC-Bridge zu .NET)

> **Status:** 🟢 Phase 0–3 **erfolgreich** — Auth, ACLs (Connect/Open/Listing) und Close-Hooks
> (Versionierung/Ownership/Index) laufen über gRPC; Datenpfad bleibt nativ
> **Autor:** Design-Dokument, erstellt 2026-07-17
> **Betrifft:** Ersatz der SMB-Protokollebene des Kaimo File Servers
> **Verwandt:** `src/Kaimo_File_Server.Smb/` (wird ersetzt),
> `src/Kaimo_File_Server.SmbBridge/` (neue gRPC-Control-Plane), `docker-compose.yml`,
> [`../samba-vfs/README.md`](../samba-vfs/README.md) (Phase-0/1-Ergebnisse)

---

## Inhalt

1. [Kontext & Motivation](#1-kontext--motivation)
2. [Zielarchitektur](#2-zielarchitektur)
3. [Aufgabenteilung: Samba nativ vs. gRPC → .NET](#3-aufgabenteilung-samba-nativ-vs-grpc--net)
4. [Was wegfällt / bleibt / neu ist](#4-was-wegfällt--bleibt--neu-ist)
5. [Risiken & offene Entscheidungen](#5-risiken--offene-entscheidungen)
6. [Umsetzung in Phasen](#6-umsetzung-in-phasen)
7. [Verifikation](#7-verifikation)
8. [Aufwand & Empfehlung](#8-aufwand--empfehlung)

---

## 1. Kontext & Motivation

Der aktuelle SMB-Zugang läuft über die **selbstgeschriebene NuGet-Lib `SMB-Server` 2.1.1**
(Projekt `Kaimo_File_Server.Smb`, Namespaces `Smb.*`). Weil es eine eigene, unvollständige
SMB-2/3-Implementierung ist, ist sie in der Praxis **unzuverlässig**: Interop-Probleme mit
Windows/macOS, Protokoll-Edge-Cases, Signing/Encryption, Durable Handles, Oplocks/Leases.

**Ziel:** die Protokollebene durch **echtes Samba (`smbd`)** ersetzen — die de-facto-Referenz-
Implementierung, die jeder SMB-Client kennt — und die Kaimo-Geschäftslogik (ACLs, Versionierung,
Suche, Ownership, Recycle) über ein **eigenes Samba-VFS-Modul in C** anbinden, das per **gRPC** mit
dem bestehenden .NET-Teil spricht. Samba läuft als **eigener Docker-Container**, deployed über
`docker compose`.

### Entschiedene Weichen

| Frage | Entscheidung | Konsequenz |
|---|---|---|
| **Datenpfad** | Samba liest/schreibt **direkt** auf dem gemounteten Storage | gRPC nur für Control-Plane → geringe Latenz, native Durchsatzleistung |
| **Auth** | Bestehende, verschlüsselte **NT-Hashes aus der DB** weiternutzen | Kompatibel (`MD4(UTF16LE(pw))` = Sambas NT-Hash), custom `pdb`-Modul nötig |
| **Rollout** | **Vollersatz** der `SMB-Server`-Lib und des `Kaimo_File_Server.Smb`-Layers | Kein Parallelbetrieb; sauberer Schnitt |

---

## 2. Zielarchitektur

```
   SMB-Clients (Windows / macOS / Linux)
              │  SMB 2/3, Port 445
              ▼
 ┌─────────────────────────────────────────┐        ┌──────────────────────────────┐
 │  Container: kaimo_samba  (NEU)           │        │  Container: kaimo_file_server │
 │  ─ smbd (echtes Samba)                   │        │  (Host, .NET)                 │
 │  ─ config backend = registry             │  gRPC  │  ─ NEU: SmbBridge gRPC-Server │
 │  ─ VFS-Modul  kaimo_bridge.so  (C/C++) ──┼───────►│    (Control-Plane)            │
 │  ─ pdb-Modul  kaimo_pdb  (NT-Hash)     ──┼───────►│  ─ IFileService / IAclService │
 │  ─ ShareControl-Daemon  ◄────────────────┼────────┤    IAuthenticationLookup      │
 │                                          │        │    IFileVersionService, Suche │
 │  mount: /data/storage  (direkte I/O)     │        │    (alles aus Core, bleibt)   │
 └───────────────┬──────────────────────────┘        └──────────────┬───────────────┘
                 │ liest/schreibt Dateien direkt                     │
                 ▼                                                   ▼
        ┌──────────────── shared volume:  /data/storage ────────────────┐
        └───────────────────────────────────────────────────────────────┘
                         (Postgres + Elasticsearch wie bisher)
```

Beide Container mounten denselben Storage. Samba macht die rohe Datei-I/O selbst; das VFS-Modul ruft
.NET nur an den „Cross-Cutting"-Stellen. .NET kann dieselben Dateien lesen (für Versionierung/Index),
weil es dasselbe Volume gemountet hat — genau wie heute Host und Web.

---

## 3. Aufgabenteilung: Samba nativ vs. gRPC → .NET

| Aufgabe | Wer erledigt es | Anmerkung |
|---|---|---|
| SMB-Protokoll, Signing, Encryption, Durable Handles, Oplocks/Leases | **Samba nativ** | genau der Grund für den Umstieg |
| Roh-Read/Write/Seek/Flush | **Samba nativ, direkt auf Disk** | kein gRPC pro Byte |
| NTLMv2-Handshake | **Samba nativ** | Hashvergleich lokal |
| NT-Hash-Beschaffung | **gRPC → .NET** | custom `pdb`-Modul (Risiko 1) |
| TREE_CONNECT-Autorisierung (Share-Zugriff) | **VFS `connect`-Hook → gRPC** | `CanAccessShareAsync` |
| ACL-Entscheidung bei Open/Create/Mkdir/Delete | **VFS-Hook → gRPC** | heute in `FileService.OpenAsync` |
| Versionierung/Snapshot beim Close | **VFS `close`-Hook → gRPC** | `IFileVersionService` |
| Suchindex + Ownership beim Close | **VFS `close`-Hook → gRPC** | Elasticsearch, Ownership-Stamp |
| Delete → Recycle-Bin | **VFS `unlink`-Hook → gRPC** *oder* nativ `vfs_recycle` | Abwägung (Risiko 4) |
| „Previous Versions" (@GMT) | **VFS Snapshot-Hooks → gRPC** | `GetSnapshotTimestampsAsync` / `OpenSnapshotAsync` |
| Directory-Listing inkl. ACL-Filter | **VFS `readdir`-Hook** (perf-sensibel) | heute filtert `FileService.ListAsync` per ACL |
| Share-Liste dynamisch verwalten | **ShareControl → `net conf` (registry)** | ersetzt heutigen FileSystemWatcher/`SyncFromDb` |
| Share-Sichtbarkeit pro User (ABE) | **offen** — siehe Risiko 2 | heute `KaimoSharePolicy.IsVisible` |

---

## 4. Was wegfällt / bleibt / neu ist

### Wegfällt (ersetzt)
- NuGet `SMB-Server` 2.1.1 und das gesamte Projekt `Kaimo_File_Server.Smb`
  (`SmbServer.cs`, `KaimoIdentityBackend.cs`, `KaimoSharePolicy.cs`, `KaimoFileStore.cs`,
  `KaimoFileHandle/Info`, `KaimoShare`, `KaimoUserRegistry`, `SmbSync`, `SmbManagedDataService`).
- Der FileSystemWatcher-basierte Share-Reconcile-Mechanismus in `SmbServer.cs`.

### Bleibt praktisch unverändert (der große Vorteil)
- `Kaimo_File_Server.Core` — Domain, `IFileService`/`FileService`, `IAclService`, Versionierung,
  Ownership, Suche. Wird nur hinter eine gRPC-Fassade statt hinter `KaimoFileStore` gesetzt.
- `Kaimo_File_Server.Infrastructure` — `FileSystemStorage`, Repositories, `FileServiceFactory`,
  `IAuthenticationLookup`, Config-Store, Migrationen (inkl. `AesGcmNtHashProtector`).
- `Kaimo_File_Server.Web` — Admin-UI (kann unverändert bleiben, wenn ShareControl die DB pollt).
- Postgres, Elasticsearch, das `/data/storage`-Volume-Modell.

### Neu
1. **.NET: gRPC-Control-Plane-Server** (neues Projekt `Kaimo_File_Server.SmbBridge` oder im Host).
   Dünne Fassade auf die bestehenden Core-Services. Wiederverwenden:
   `IFileServiceFactory.CreateForShare(shareId, path)`, `IFileService.OpenAsync/ListAsync/…`,
   `IAuthenticationLookup.GetNtHashAsync/ResolveUserContextAsync`, `IAclService`, `IFileVersionService`.
2. **C-Projekt `samba-vfs/`**: VFS-Modul `kaimo_bridge` (C + C++-TU für den gRPC-Client mit
   `extern "C"`-Shim), custom `pdb`-Modul, ShareControl-Daemon, `smb.conf`-Vorlage mit
   `config backend = registry`, Entrypoint.
3. **Gemeinsame `.proto`-Dateien** — einmal definiert, für .NET (`Grpc.Tools`) und C/C++
   (`protoc` + `grpc_cpp_plugin`) generiert.
4. **`samba-vfs/Dockerfile`** + neuer compose-Service `kaimo_samba` (Port 445, mount `/data/storage`);
   Port 445 wandert vom Host- zum Samba-Container.

---

## 5. Risiken & offene Entscheidungen

### Risiko 1 — Auth: NT-Hash-Reuse braucht ein custom Samba-`pdb`-Backend
Samba validiert NTLMv2 lokal, braucht den NT-Hash aber aus seiner `passdb`. Kaimos NT-Hash ist
`MD4(UTF16LE(pw))` — **exakt Sambas NT-Hash**, also kompatibel. Zwei Wege:
- *(empfohlen)* **custom `pdb`-Modul** (`pdb_methods`, v. a. `getsampwnam`), holt den Hash live per
  gRPC von .NET → eine Quelle der Wahrheit. Kosten: C gegen Samba-Interna + SID-/Flag-Mapping.
- *(Fallback)* NT-Hashes periodisch in Sambas `tdbsam` **synchronisieren**. Einfacher, aber
  Sync-Job nötig und zweite Datenhaltung.

> **In Phase 1 umgesetzt (Fallback-Weg):** Die .NET-Bridge (`Kaimo_File_Server.SmbBridge`) liefert
> per gRPC `ListUsers`/`GetNtHash` die NT-Hashes; der C++-Client `kaimo_authsync` im Samba-Container
> importiert sie via `pdbedit` in `tdbsam`. Echter NTLMv2-Login (`admin/admin1234`,
> `marco.hanisch/1234`) funktioniert, Falschpasswort wird abgelehnt. Das custom `pdb`-Modul (on-demand,
> ohne Bulk-Sync) bleibt die spätere Produktions-Option. **Nebenbei erledigt:** gRPC-in-C++ im
> Samba-Container ist damit bewiesen — das letzte offene Toolchain-Risiko aus Phase 0.

### Risiko 2 — Dynamische Share-*Sichtbarkeit* pro User ⚠️ (kniffligster Punkt)
- *Shares dynamisch existieren lassen* → **gelöst** über `config backend = registry` +
  `registry shares = yes`: `smbd` liest Shares live aus `registry.tdb`, **ohne Reload/Restart**. Der
  ShareControl-Daemon setzt sie per `net conf addshare/setparm/delshare`. Ersetzt `SyncFromDb()`.
- *Pro-User-ABE-Sichtbarkeit* (heute `KaimoSharePolicy.IsVisible` → `CanListShareAsync`) →
  **der echte Knackpunkt.** Share-Enumeration läuft über `srvsvc`/IPC$ *bevor* ein Share-VFS aktiv
  ist; kein sauberer VFS-Hook. Optionen:
  - **(a)** natives `access based share enum = yes` + Share-ACL aus Kaimo in die Registry sync'en.
  - **(b)** nur grobe `IsShareHidden`-Semantik (`browseable = no`), feingranulares Listing aufgeben.
  - **(c)** Kompromiss: (a) für Sichtbarkeit + `connect`-Hook für die harte Autorisierung.
  - → **Empfehlung: (c).** Vor Umsetzung als bewusste Produkt-Entscheidung festhalten.

### Risiko 3 — VFS-ABI-Kopplung + gRPC-in-C-Toolchain
VFS-Module müssen gegen die **exakte Samba-Version** (`SMB_VFS_INTERFACE_VERSION`) kompiliert werden;
die internen Header (`vfs.h` u. a.) liegen **nicht** in `samba-dev`, sondern nur im Quellbaum. →
Modul im selben Image gegen dieselbe Samba-Quelle bauen; Samba-Upgrade kann Neubau erfordern
(Wartungskosten). gRPC hat in reinem C nur eine Low-Level-API → praktikabel ist **gRPC-C++** in einer
C++-TU mit `extern "C"`-Shim. Da die Control-Plane **niederfrequent** ist (open/close/connect, nicht
pro Byte), ist der Transport unkritisch — Alternative: Protobuf über Unix-Socket (nanopb).

> **In Phase 0 bestätigt:** `samba-dev` liefert die VFS-Header nicht (Out-of-Tree-Build unmöglich),
> und ein upstream-gebautes Modul lädt **nicht** in die Distro-Samba
> (`libsmbd-base-samba4.so: cannot open shared object`). Konsequenz: **Samba selbst bauen und
> betreiben** (`--prefix=/opt/samba`) — Modul, `smbd` und private Libs aus einem Build. Das Kaimo-
> Samba-Image pinnt damit ohnehin die Samba-Version, was die ABI-Kopplung entschärft.

### Risiko 4 — Feature-Parität an drei Stellen
- **Snapshots/@GMT:** Snapshot-VFS-Modul (`FSCTL_SRV_ENUMERATE_SNAPSHOTS` → gRPC) *oder* Kaimos
  Versionslayout so ablegen, dass das native `vfs_shadow_copy2` es versteht.
- **Recycle:** prüfen, ob das native `vfs_recycle` reicht (weniger Code) oder ob Kaimos
  `.RECYCLE_BIN`/`IsRecycleEnabled`-Verhalten den `unlink`-Hook → gRPC braucht.
- **Directory-Listing-ACL-Filter:** heute versteckt `FileService.ListAsync` Einträge ohne Recht. Ein
  `readdir`-Hook mit gRPC pro Eintrag wäre teuer → Batch-Filter/Caching. Perf-sensibel, früh messen.

---

## 6. Umsetzung in Phasen

| Phase | Inhalt | Ziel |
|---|---|---|
| **0 — Spike/PoC** ✅ | `samba-vfs/`-Container: Live-Registry-Shares + `kaimo_bridge`-Modul, das Connect/Open/Disconnect abfängt | Toolchain + ABI-Bindung + Live-Shares **bewiesen** — siehe [`../samba-vfs/README.md`](../samba-vfs/README.md) |
| **1 — Auth** ✅ | `.proto` + .NET-gRPC-Bridge (`GetNtHash`/`ListUsers` über `IAuthenticationLookup`); C++-Client `kaimo_authsync` synct NT-Hashes in Sambas `tdbsam` | echter NTLMv2-Login gegen Kaimo-User **läuft** — siehe [`../samba-vfs/README.md`](../samba-vfs/README.md) |
| **2a — Connect-Autz** ✅ | VFS-`connect`-Hook → Sidecar `kaimo_authd` (Unix-Socket) → gRPC `AuthorizeConnect` → `CanAccessShareAsync` | Share-Zugriff nach echten Kaimo-ACLs — **läuft** (dept-basiertes Allow/Deny verifiziert) |
| **2b — Open/Path-ACL** ✅ | VFS-`create_file`-Hook → gRPC `AuthorizeOpen` (exakte `OpenAsync`-Parität) + `readdir`-Filter (`ListAsync`-Parität, Sidecar-Cache) | Datei-Open (read/write/create) und Listing nach echten ACLs — **läuft** (write-Deny, per-Datei-Deny + Hiding verifiziert) |
| **3 — Close-Hooks** ✅ | `close`/`unlinkat`/`renameat`/`mkdirat` → Sidecar → gRPC `EventService` → `FileService.NotifyExternal*` (Versionierung, Ownership, Suchindex, ACL-Realign) | Parität zu `FileSession.DisposeAsync` — **läuft** (Versionierung/Ownership verifiziert; s. [`../samba-vfs/README.md`](../samba-vfs/README.md)) |
| **4 — Dyn. Shares & Sichtbarkeit** | ShareControl-Daemon → `net conf`; ABE nach Risiko-2-Empfehlung; Protokoll-Settings aus `ISmbConfigStore` | dynamische Shares live |
| **5 — Snapshots & Cutover** | @GMT-Mapping; `enable/disable SMB` auf `smbd` umbiegen; `Kaimo_File_Server.Smb` entfernen; compose finalisieren | alte Lib raus |

---

## 7. Verifikation (End-to-End)

- `docker compose up`, dann von **Windows-Explorer + macOS Finder + `smbclient`** gegen
  `\\host\<share>`: Login mit Kaimo-User (NTLMv2), Ordner/Dateien anlegen, lesen, schreiben,
  umbenennen, löschen.
- **ACL:** User ohne Recht bekommt `AccessDenied` bei Open/Connect (gRPC-Entscheidung greift).
- **Versionierung:** Datei mehrfach speichern → „Vorgängerversionen" (@GMT) sichtbar; ES-Index
  aktualisiert; Ownership gestempelt.
- **Dynamik:** über Web-UI Share anlegen/umbenennen/löschen → erscheint/verschwindet live ohne
  Neustart; versteckte/ABE-Shares korrekt (un)sichtbar.
- **Robustheit:** `smbtorture`/`smbclient`-Interop, Signing/Encryption erzwungen — der Kern-Grund.
- **Perf:** Latenz bei vielen kleinen Dateien / großen Directory-Listings messen (Risiko 4).

---

## 8. Aufwand & Empfehlung

Substanzielles Vorhaben, **mehrere Wochen**. Risiko konzentriert auf: (1) custom `pdb`-Backend,
(2) Per-User-ABE-Sichtbarkeit, (3) VFS-ABI/gRPC-in-C-Toolchain, (4) Snapshot/@GMT & Listing-Filter.
Der Rest ist gut kalkulierbares Integrations-Handwerk, weil die Kaimo-Kernlogik in `Core`/
`Infrastructure` erhalten bleibt und nur neu „verdrahtet" wird.

> **Empfehlung: Phase 0 zuerst** — sie klärt die zwei größten Unbekannten (Build-Toolchain +
> Live-Registry-Shares) mit minimalem Einsatz, bevor größere Investition erfolgt.

Der Fortschritt von Phase 0 wird in [`samba-vfs/README.md`](../samba-vfs/README.md) festgehalten.
