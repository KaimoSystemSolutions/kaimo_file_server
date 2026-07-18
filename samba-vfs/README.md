# samba-vfs — Phase-0-Spike (Proof-of-Concept)

Ziel dieses Verzeichnisses: **beweisen, dass der Samba+VFS-Ansatz überhaupt trägt**, bevor größer
investiert wird. Siehe Gesamtplan: [`../docu/smb-samba-vfs-migration.md`](../docu/smb-samba-vfs-migration.md).

Phase 0 klärt die zwei größten Unbekannten:

| # | Unbekannte | Status |
|---|---|---|
| A | Dynamische Shares live über `net conf` (Registry-Backend), ohne smbd-Neustart | ✅ **bewiesen** |
| B | Eigenes VFS-Modul gegen die exakte Samba-ABI bauen **und** von smbd laden | ✅ **bewiesen** |

**Fazit Phase 0: der Ansatz trägt.** Beide Kern-Unbekannten sind geklärt. Der Weg nach vorne
(Phasen 1–5) ist im [Gesamtplan](../docu/smb-samba-vfs-migration.md) beschrieben.

---

## Schritt A — Live-Registry-Shares (Stock-Samba)

**Ergebnis: funktioniert.** Ein per `net conf addshare` angelegter Share erscheint sofort in
`smbclient -L`, **ohne** dass smbd neu gestartet wird; Schreiben/Lesen über SMB3 landet direkt auf
dem Storage. Das ersetzt später den FileSystemWatcher/`SyncFromDb`-Mechanismus aus
`src/Kaimo_File_Server.Smb/SmbServer.cs`.

```bash
# Image bauen und starten
docker build -t kaimo-samba-spike:phase0 .
docker run -d --name kaimo-samba-spike -p 1445:445 kaimo-samba-spike:phase0

# Selbsttest (legt Share live an, schreibt/liest, entfernt ihn wieder)
docker exec kaimo-samba-spike bash /usr/local/bin/selftest.sh
```

Dateien: [`Dockerfile`](Dockerfile), [`conf/smb.conf`](conf/smb.conf),
[`entrypoint.sh`](entrypoint.sh), [`selftest.sh`](selftest.sh).

Kern der Konfig (`smb.conf`):
```ini
registry shares = yes
include = registry           # smbd liest Shares LIVE aus registry.tdb
```

---

## Schritt B — Eigenes VFS-Modul

**Ergebnis: funktioniert.** `smbd` lädt unser Modul und die Hooks feuern bei jedem Connect/Open —
auf einem dynamisch per `net conf` angelegten Share:

```
kaimo_bridge: CONNECT service=[hooktest] user=[kaimotest]   <- TREE_CONNECT
kaimo_bridge: OPENAT  name=[x.txt]                          <- jeder Datei-Open
kaimo_bridge: DISCONNECT
```

Genau diese drei Nähte sind die späteren gRPC-Aufrufpunkte zur .NET-Control-Plane
(`CanAccessShareAsync` beim Connect, ACL-Entscheidung beim Open, Close-Hooks für Versionierung/Index).
Die eigentliche I/O bleibt nativ (`SMB_VFS_NEXT_*`) — die Datei landet direkt auf dem Storage.

### Zwei zentrale Befunde (bestätigen Risiko 3)

1. **`samba-dev` reicht nicht.** Das Distro-Dev-Paket (4.19.5) liefert die internen VFS-Header
   **nicht** (kein `vfs.h` mit `SMB_VFS_INTERFACE_VERSION`, kein `smb_register_vfs`). Ein
   Out-of-Tree-Build gegen Distro-Header ist unmöglich → Bau gegen den **Quellbaum**.
2. **Modul + smbd müssen aus demselben Build stammen.** Ein gegen die Upstream-Quelle gebautes Modul
   in die *Distro*-Samba einzusetzen scheitert an
   `libsmbd-base-samba4.so: cannot open shared object file` (Ubuntu benennt die privaten Samba-Libs
   um). → Wir **bauen und betreiben Samba selbst** (`--prefix=/opt/samba`). Das ist zugleich der
   spätere Produktionspfad, weil wir die Samba-Version ohnehin kontrollieren müssen (ABI 49).

### Bauen & testen

```bash
# Voraussetzung: Quell-Image mit dem Samba-Quellbaum (einmalig, cacht den Download)
#   docker build -f Dockerfile.src -t kaimo-samba-src:4.19.5 .
# Samba + Modul aus einer Quelle bauen (~7 min: 2:30 build, 4:10 install)
docker build -f Dockerfile.vfs -t kaimo-samba-spike:vfs .
docker run -d --name kaimo-samba-vfs -p 1446:445 kaimo-samba-spike:vfs
# Datei-Op erzwingen und Hooks im Log nachweisen
docker exec kaimo-samba-vfs bash /usr/local/bin/selftest.sh
docker logs kaimo-samba-vfs 2>&1 | grep "kaimo_bridge:"
```

Dateien: [`Dockerfile.vfs`](Dockerfile.vfs), [`module/vfs_kaimo_bridge.c`](module/vfs_kaimo_bridge.c),
[`conf/smb.conf.vfs`](conf/smb.conf.vfs), [`entrypoint.vfs.sh`](entrypoint.vfs.sh).

> Das Zwischen-Image `kaimo-samba-src:4.19.5` (siehe [`Dockerfile.src`](Dockerfile.src)) cacht nur
> den entpackten Samba-Quellbaum, damit der Modul-Build nicht bei jeder Iteration neu herunterlädt.

---

## Phase 1 — Auth (echter NTLMv2-Login gegen Kaimo-User)

**Ergebnis: funktioniert.** Ein Kaimo-Benutzer meldet sich mit seinem echten Passwort per NTLMv2 an;
ein falsches Passwort wird abgelehnt. Die NT-Hashes stammen live aus der Kaimo-DB.

**Fluss:**

```
 Kaimo-DB ──► SmbBridge (.NET gRPC, :5080 h2c) ──gRPC ListUsers──► kaimo_authsync (C++)
                 IAuthenticationLookup.GetNtHashAsync                 │  username + NT-Hash
                 (entschlüsselt, filtert deaktiviert/leer)            ▼
                                                          sync-users.sh ──pdbedit──► Sambas tdbsam
                                                                                        │
                                             smbd prüft NTLMv2 lokal gegen den NT-Hash ─┘
```

- **.NET-Seite:** neues Projekt `src/Kaimo_File_Server.SmbBridge` (ASP.NET-gRPC). Dünne Fassade über
  das bestehende `IAuthenticationLookup` — keine Auth-Logik dupliziert. Braucht denselben
  `NtHash__EncryptionKey` wie Host/Web (in `docker-compose.override.yml` gesetzt).
- **C++-Seite:** `module/authsync.cpp` (gRPC-C++-Client) + [`sync-users.sh`](sync-users.sh). Der
  Entrypoint synct beim Start (mit Retries) und danach alle 60 s. **Damit ist auch gRPC-in-C++ im
  Samba-Container bewiesen** — das letzte offene Toolchain-Risiko aus Phase 0.
- **Proto-Vertrag:** [`protos/kaimo_smb_bridge.proto`](protos/kaimo_smb_bridge.proto) — einmal
  definiert, generiert C#- (Bridge) und C++-Stubs (authsync).

**Testen (Kurzform):**
```bash
docker compose up -d kaimo_smb_bridge kaimo_samba
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  smbclient -L localhost -U admin%admin1234 -m SMB3;      # RICHTIG  -> Shares
  smbclient -L localhost -U admin%falsch    -m SMB3'      # FALSCH   -> NT_STATUS_LOGON_FAILURE
```
> Voraussetzung: die Demo-User sind geseedet (`Seed:DemoData=true`, dev): `admin/admin1234`,
> `marco.hanisch/1234`, `anna.weber/1234`, `lisa.mueller/1234`.

**Offen für Phase 2:** Die Autorisierung (Share-Zugriff, ACL beim Open) läuft noch nicht über gRPC —
die VFS-Hooks (`connect`/`openat`) protokollieren bisher nur. Als Nächstes rufen sie
`CanAccessShareAsync` / `FileService.OpenAsync` über dieselbe Bridge.

## Phase 2a — Connect-Autorisierung (echte Kaimo-ACLs entscheiden den Share-Zugriff)

**Ergebnis: funktioniert.** Beim TREE_CONNECT entscheidet die echte Kaimo-ACL, ob ein Benutzer den
Share betreten darf — dieselbe Semantik wie das frühere `KaimoSharePolicy.AuthorizeConnect`.

**Fluss:**

```
 smbd VFS connect-Hook (kaimo_bridge.so, reines C)
   │  Unix-Socket:  "CONNECT\t<user>\t<share>"
   ▼
 kaimo_authd (Sidecar, C++)  ──gRPC AuthorizeConnect──►  SmbBridge (.NET)
   │  "ALLOW" / "DENY"                                     CanAccessShareAsync(shareId, userId)
   ▼
 erlauben -> SMB_VFS_NEXT_CONNECT   |   ablehnen -> errno=EACCES, TREE_CONNECT scheitert
```

- **Warum ein Sidecar?** So bleibt das smbd-VFS-Modul reines C (nur ein Socket-Roundtrip) — kein
  gRPC/Threads/Fork im smbd-Prozess. Die gRPC-Komplexität kapselt `kaimo_authd`
  ([`module/authd.cpp`](module/authd.cpp)).
- **.NET:** [`AuthzGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/AuthzGrpcService.cs) löst
  User-/Share-Namen auf Guids auf und ruft `IAuthenticationLookup.CanAccessShareAsync`.
- **IPC$** wird immer zugelassen (Share-Enumeration).

**Verifiziert (dept-basiertes Allow/Deny, entspricht exakt den Kaimo-ACLs):**

| User (Abteilung) | Share | Entscheidung |
|---|---|---|
| marco.hanisch (Frontend) | frontend-docs | **ALLOW** |
| marco.hanisch (Frontend) | marketing-files | **DENY** |
| lisa.mueller (Marketing) | marketing-files | **ALLOW** |
| lisa.mueller (Marketing) | frontend-docs | **DENY** |
| anna.weber (Backend) | backend-docs | **ALLOW** |
| admin (nur Management-Rolle) | jeder Share | **DENY** |

> **Hinweis admin:** `admin` hat die Management-Rolle „Administrator", aber **keine Datei-ACLs** auf
> den Shares. `CanAccessShareAsync` verweigert daher — **genau wie der alte SMB-Stack** (getreue
> Parität, kein Fehler). Datei-Zugriff wird über ACLs/Departments vergeben, nicht über die
> Management-Rolle.

**Fail-Verhalten:** Ist der Sidecar/die Bridge nicht erreichbar, erlaubt das Modul standardmäßig
(fail-open, damit ein Ausfall nicht alles sperrt). Mit `KAIMO_AUTHZ_FAILCLOSED=1` wird strikt
abgelehnt.

**Bekannte Kosmetik:** Ein Deny erscheint clientseitig als `NT_STATUS_UNSUCCESSFUL` (nicht
`ACCESS_DENIED`) — mehrere Samba-Codepfade hartkodieren das bei VFS-connect-Fehlern. Funktional ist
der Zugriff korrekt verweigert.

**Offen (Phase 2b):** Datei-/Pfad-ACL beim `openat`/`unlink`/`rename` (heute nur Logging) und der
Directory-Listing-Filter — perf-sensibel und mit Pfad-Rekonstruktion verbunden, daher als eigener
Schritt.

## Phase 2b — Datei-/Pfad-ACL + Listing-Filter (Phase 2 komplett)

**Ergebnis: funktioniert.** Datei-Open (read/write/create) und Directory-Listing folgen den echten
Kaimo-ACLs — mit **exakter Parität** zum alten `FileService.OpenAsync` bzw. `ListAsync`.

- **`create_file`-Hook** ([vfs_kaimo_bridge.c](module/vfs_kaimo_bridge.c)) → gRPC `AuthorizeOpen`.
  Der richtige Seam (nicht `openat`): voller Pfad + Access-Mask, gibt sauberes `ACCESS_DENIED`.
  Paritätslogik in [`AuthzGrpcService.AuthorizeOpen`](../src/Kaimo_File_Server.SmbBridge/Services/AuthzGrpcService.cs):
  write→`CreateWriteData`, read→`ListReadData`, create→`CreateWriteData` auf dem Parent; jede
  Zugriffsart einzeln; nicht-existent ohne Create = „not found" (kein ACL-Deny).
- **`readdir`-Hook** → verbirgt Einträge ohne Leserecht (`AuthorizeOpen` read-only pro Eintrag).
  Der **Sidecar cached** Entscheidungen (TTL 3 s), damit große Listings die Bridge nicht fluten.
  Abschaltbar mit `KAIMO_LIST_FILTER=0`.

**Verifiziert:**

| Fall | Ergebnis |
|---|---|
| lisa (Marketing = nur-lesend) schreibt in marketing-files | **DENY** (`create_file` → `CreateWriteData`) |
| marco (Entwicklung = schreibend) schreibt in frontend-docs | **ALLOW**, Datei auf Disk |
| marco liest Datei mit explizitem Deny-ACL | **DENY** (`ACCESS_DENIED`) |
| marco listet Verzeichnis mit Deny-Datei | Datei **unsichtbar** (Listing-Filter), liegt aber real auf Disk |

> **Wichtig — Storage-Mount:** Die Bridge braucht denselben `/data/storage`-Mount (Existenz-/Typ-
> Prüfung wie `OpenAsync`). In [`../docker-compose.yml`](../docker-compose.yml) ist er gesetzt. Ohne
> ihn behandelt die Bridge existierende Dateien als „not found" und erlaubt zu viel.

**Noch offen für spätere Phasen:** Snapshots/@GMT (Phase 5), Recycle-Bin/Versionierung/Suchindex an
den Close-Hooks (Phase 3), sowie das durchgehende `NT_STATUS_ACCESS_DENIED` beim connect (heute
`NT_STATUS_UNSUCCESSFUL`, Samba-intern).

## Phase 3 — Close-Hooks (Versionierung, Ownership, Suchindex)

**Ergebnis: funktioniert.** Samba führt die Datei-I/O nativ aus und meldet danach das Ereignis; die
Bridge erledigt dieselben Cross-Cutting-Effekte wie früher `FileSession.DisposeAsync`.

**Fluss:**

```
 smbd VFS-Hook (reines C)          Sidecar (kaimo_authd)        SmbBridge (.NET)
  close_fn   (Datei geschrieben) ──"CLOSE\t…"──► NotifyClose ──► FileService.NotifyExternalCloseAsync
  unlinkat_fn(geloescht)         ──"DELETE\t…"─► NotifyDelete ─►   → Version (CreateVersionAsync)
  renameat_fn(umbenannt)         ──"RENAME\t…"─► NotifyRename ─►   → Ownership (EnsureOwnerAsync)
  mkdirat_fn (Verz. angelegt)    ──"MKDIR\t…"──► NotifyMkdir  ─►   → Suchindex (SearchServiceRouter)
                                    (fire-and-forget)               → ACL-Realign (Rename)
```

- **.NET:** neue `FileService.NotifyExternal{Close,Delete,Rename,Mkdir}Async` (in Core) nutzen die
  **bereits verdrahteten** Version-/Ownership-/Such-Services — identische Ergebnisse wie Web-Uploads.
  Fassade: [`FileEventGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/FileEventGrpcService.cs).
- **Sidecar** ist jetzt **multi-threaded** (Thread pro Verbindung), damit langsame Events
  (Versionierung liest die Datei) die Authz-Anfragen nicht blockieren. Events sind fire-and-forget.
- **Recycle-Bin** bewusst **nicht** implementiert: der alte SMB-Pfad (`MarkDeleteOnClose`) recycelt
  ebenfalls nicht — Recycle gibt es nur im Web-`DeleteFileAsync`. Das ist also getreue Parität.

**Verifiziert:**

| Ereignis | Ergebnis |
|---|---|
| marco schreibt Datei → schließt | `file_versions`-Snapshot angelegt (**Versionierung** ✅) |
| dieselbe Datei | `file_metadata.OwnerId = marco` (**Ownership** ✅) |
| Datei löschen | `NotifyDelete` gefeuert → Deindex-Pfad ✅ |
| Datei umbenennen | `NotifyRename` gefeuert → ACL-Realign + Index-Pfad ✅ |

**Bekannte Punkte:**

- **Suchindex:** Die Indizierung läuft über denselben `SearchServiceRouter` wie der Host. Der Router
  gated Elasticsearch über einen Erreichbarkeits-Ping; auf diesem System ist ES-Indexing aktuell
  **systemweit inaktiv** (auch der Host indiziert seit dem 9. Juli nichts Neues — unabhängig von
  dieser Migration). Die Bridge verhält sich also **parität-treu**. Sobald ES-Indexing wieder aktiv
  ist, werden SMB-Writes wie Web-Uploads indiziert.
- **Rasch aufeinanderfolgende Schreibvorgänge** derselben Datei können zu einer Version
  zusammenfallen: die Version wird beim Close aus der Datei **nachgelesen** (nicht aus dem offenen
  Stream wie im alten In-Process-Pfad). Für normale Speicherabstände unkritisch.
- **`mkdirat`-Hook** feuert in Sambas SMB2-Verzeichnis-Erstellungspfad nicht zuverlässig (Dirs werden
  offenbar nicht immer über `mkdirat_fn` angelegt) — kleiner Rand-Gap, Datei-Ops sind vollständig.

## Phase 4 — Dynamische Shares (Registry-Provisioning aus der Kaimo-DB)

**Ergebnis: funktioniert.** Die in der Kaimo-DB *aktivierten* Shares erscheinen automatisch in
Samba — ohne smbd-Neustart —, und Anlegen/Umbenennen/Löschen/Deaktivieren über die Web-UI schlägt
live durch. Das ersetzt den FileSystemWatcher/`SyncFromDb()`-Mechanismus aus
`src/Kaimo_File_Server.Smb/SmbServer.cs`.

**Fluss (Spiegelbild zum NT-Hash-Sync aus Phase 1):**

```
 Kaimo-DB ──► SmbBridge (.NET gRPC, :5080) ──gRPC ListShares──► kaimo_sharesync (C++)
                 IShareRepository.GetAllEnabledAsync                 │  name<TAB>path<TAB>hidden
                 (nur aktivierte Shares)                             ▼
                                                          sync-shares.sh ──net conf──► registry.tdb
                                                            (add/setparm/delshare)        │
                                                    smbd liest Shares LIVE aus Registry ──┘
```

- **.NET:** [`ShareGrpcService`](../src/Kaimo_File_Server.SmbBridge/Services/ShareGrpcService.cs) —
  dünne Fassade über `IShareRepository`; keine Share-Logik dupliziert (deaktivierte Shares filtert
  bereits das Repository). Registriert in [`Program.cs`](../src/Kaimo_File_Server.SmbBridge/Program.cs).
- **C++:** [`module/sharesync.cpp`](module/sharesync.cpp) (gRPC-Client) +
  [`sync-shares.sh`](sync-shares.sh). Der Entrypoint synct beim Start (mit Retries, bis die Bridge
  erreichbar ist) und danach alle 60 s — wie der User-Sync.
- **Reconciliation** in `sync-shares.sh` ist idempotent: neue Shares → `net conf addshare`,
  geänderte (Pfad/Sichtbarkeit) → `net conf setparm`, entfernte/deaktivierte → `net conf delshare`.
  `global` wird nie angefasst.

### Sichtbarkeit (ABE) — Entscheidung: nur Hidden-Flag

`IsShareHidden` → `browseable = no` (der Share verschwindet aus der Auflistung, bleibt aber per
`\\host\share` direkt erreichbar). Der **harte** Share-Zugriff wird unverändert vom Phase-2a-
`connect`-Hook nach echten Kaimo-ACLs entschieden. Volle Per-User-ABE (`valid users` pro Share)
wurde bewusst **nicht** umgesetzt — sie würde sich mit dem `connect`-Hook überschneiden und die
ACL-Logik doppeln. Details/Begründung: [Risiko 2 im Gesamtplan](../docu/smb-samba-vfs-migration.md#risiko-2--dynamische-share-sichtbarkeit-pro-user--kniffligster-punkt).

**Testen (nachdem die Web-UI/DB Shares enthält):**
```bash
docker compose up -d kaimo_smb_bridge kaimo_samba
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  /usr/local/bin/sync-shares.sh;               # Registry aus der DB spiegeln
  net conf listshares;                         # -> die aktivierten Kaimo-Shares
  smbclient -L localhost -U admin%admin1234 -m SMB3'   # -> Shares in der Enumeration
```

### Protokoll-Settings aus der Kaimo-DB (`ISmbConfigStore`)

**Ergebnis: funktioniert.** Die per Web-UI gepflegten SMB-Protokoll/Sicherheits-Optionen
(Dialekt-Range, Signing, Encryption) landen in Sambas globaler Config — dasselbe, was
`SmbServer.LoadProtocolSettings()` beim (Re)Start in den alten .NET-SMB-Server einspeiste.

**Fluss** (analog zum Share-Sync):

```
 Kaimo-DB ──► SmbBridge (:5080) ──gRPC GetProtocolSettings──► kaimo_configsync (C++)
                 ISmbConfigStore.GetProtocolSettingsAsync         │  min⇥max⇥signing⇥encrypt
                 (frisch, ohne Cache)                             ▼
                                              sync-config.sh ──net conf setparm global──► registry.tdb
                                                (nur bei Aenderung: smbcontrol smbd reload-config)
```

- **Mapping** (in der Bridge, damit die Shell samba-agnostisch bleibt): Dialekt-Enum → `server min/max
  protocol` (`SMB2_02`…`SMB3_11`); `RequireSigning` → `server signing = mandatory|auto`;
  `RequireEncryption` → `smb encrypt = required|default`.
- **Präzedenz:** In [`conf/smb.conf.vfs`](conf/smb.conf.vfs) steht `include = registry` **am Ende** der
  `[global]`-Sektion, damit die per `net conf` gesetzten DB-Werte die Inline-Fallback-Defaults
  überschreiben. smbd liest sie beim Start bzw. nach `smbcontrol smbd reload-config` (nur neue
  Verbindungen; bestehende bleiben). Der Config-Sync löst den Reload **nur bei tatsächlicher
  Änderung** aus.
- **Bewusst nicht synchronisiert:** WS-Discovery und Audit-Log haben in Samba keine globalen
  smb.conf-Parameter (separate Mechanismen: `wsdd` bzw. der `full_audit`-VFS; das Kaimo-VFS-Modul
  loggt Connect/Open/Close ohnehin selbst).

**Testen:**
```bash
docker compose exec kaimo_samba bash -lc '\
  export PATH=/opt/samba/sbin:/opt/samba/bin:$PATH; \
  /usr/local/bin/sync-config.sh; \
  net conf getparm global "server min protocol"; \
  net conf getparm global "server signing"'
```

### Bekanntes Problem — Schreibrechte über SMB (Storage-Ownership)

**Symptom:** Lesen über SMB geht, **Schreiben scheitert** mit `ACCESS_DENIED` (die Web-UI schreibt
normal). Ursache: Samba macht die Datei-I/O **als der authentifizierte Unix-User** (jeder Kaimo-User
bekommt via `sync-users.sh` ein POSIX-Konto mit eigener UID). Die Share-Verzeichnisse legt aber der
**Host/Web-Container** an — er läuft als `$APP_UID` (**1654**, der Standard-`app`-User der .NET-Images)
und erzeugt sie mit Modus `0755`. Also darf nur uid 1654 schreiben; die SMB-User (andere UIDs) dürfen
nur lesen. Die Kaimo-ACL sagt sogar ALLOW — erst der Kernel wirft `EACCES`.

> **Sackgassen (nicht verwenden):** Sambas `force user` **und** „alle User teilen dieselbe UID"
> lösen zwar die Schreibrechte, **zerstören aber Sambas Per-User-Identität** (SID wird algorithmisch
> aus der UID abgeleitet, plus `getpwuid`-Ruecklookups) → alle User kollabieren auf einen Principal →
> **Auth/Connect bricht**. Beides wurde probiert und wieder verworfen.

**Fix (umgesetzt):** Per-User-Identität behalten (distinkte UIDs), das Schreibrecht über eine
**gemeinsame Gruppe + gruppen-schreibbaren Storage** lösen. Konkret:

- [`entrypoint.vfs.sh`](entrypoint.vfs.sh) legt beim Start die Gruppe `kaimo` mit der Storage-GID an
  (`KAIMO_STORAGE_GID`, Default **1654**), setzt die Share-Verzeichnisse auf `2775` (setgid + g+w) und
  zieht die Gruppe/Rechte rekursiv nach (die internen Dot-Dirs `.dp-keys`/`.certs` bleiben ausgenommen).
- [`sync-users.sh`](sync-users.sh) nimmt **jeden** synchronisierten Kaimo-User per `usermod -aG` in die
  Gruppe auf — als **sekundäre** Gruppe, die primäre UID/Gruppe (und damit SID/Identität) bleibt.
- [`sync-shares.sh`](sync-shares.sh) setzt neu provisionierte Shares direkt auf Gruppe + `2775`.
- [`conf/smb.conf.vfs`](conf/smb.conf.vfs) erzwingt gruppen-schreibbare neue Objekte:
  `create mask = 0664` / `force create mode = 0060` / `directory mask = 2775` / `force directory mode = 0070`.

`force user` / geteilte UIDs bleiben tabu (siehe Sackgassen oben). Verifiziert: SMB-Write als
`marco.hanisch` auf einen `0755`-Share (`crazyFrog`) → vorher `ACCESS_DENIED`, nachher Datei mit Modus
`0664`, Gruppe `kaimo`.

**Rest-Gap:** POSIX-ACLs sind auf dem Storage-FS dieses Setups **nicht** verfügbar (`setfacl` schlägt
fehl), daher greift der Default-ACL-Weg nicht. Für **künftig vom Web** (uid 1654) angelegte Dateien
hängt das Gruppen-Schreibrecht noch an der Host-umask des Web/Host-Containers — mit `022` werden sie
`0644` (Gruppe nur lesend), sodass SMB sie zwar lesen, aber nicht überschreiben kann. Fix dafür:
**umask `002`** im Web/Host-Container. Bereits auf Disk liegende Dateien wurden beim Rollout einmalig
auf `g+rwX` gezogen und sind unkritisch.

## Deployment über docker compose

Der Spike ist als Service `kaimo_samba` in die zentrale [`../docker-compose.yml`](../docker-compose.yml)
eingebunden — `docker compose up` zieht ihn mit hoch, **ohne** die bestehende .NET-SMB-Implementierung
zu stören:

```bash
cd ..                       # ins Verzeichnis mit docker-compose.yml
docker compose up -d kaimo_samba          # nur Samba
# oder alles zusammen:
docker compose up -d
```

- **Port:** Host-Port **1445** → Container-445 (der .NET-Host behält vorerst 445; beim Cutover in
  Phase 5 übernimmt Samba die 445).
- **Storage:** derselbe Bind-Mount `./tests/data/storage:/data/storage` wie Host/Web → Samba macht die
  Datei-I/O direkt.
- **Healthcheck:** meldet `healthy`, sobald `smbd` Verbindungen annimmt.

Testen nach dem Hochfahren:
```bash
docker compose exec kaimo_samba bash /usr/local/bin/selftest.sh
docker compose logs kaimo_samba | grep "kaimo_bridge:"
```

> Der erste Build kompiliert Samba aus der Quelle (~7 min). Danach greift der BuildKit-Layer-Cache.

## Wichtige Fakten aus Phase 0

- Samba-Version Runtime: **4.19.5-Ubuntu** (`ubuntu:24.04`-Paket).
- **`SMB_VFS_INTERFACE_VERSION = 49`** — das VFS-Modul muss gegen genau diese ABI gebaut werden.
- VFS-Modulverzeichnis: `/usr/lib/x86_64-linux-gnu/samba/vfs/`.
- Modul-Init-Symbol: `vfs_kaimo_bridge_init` → registriert unter dem Namen `kaimo_bridge`.
- Registrierung im Build: `bld.SAMBA3_MODULE('vfs_kaimo_bridge', subsystem='vfs', …)` in
  `source3/modules/wscript_build`, plus Eintrag in `default_shared_modules` in `source3/wscript`.

## Nächste Schritte (nach Phase 0)

Phase 1 (Auth) — `.proto` + gRPC `GetNtHash`/`ResolveUser` in .NET, custom `pdb`-Modul; danach
Zugriff/I/O-Hooks (Phase 2). Details im [Gesamtplan](../docu/smb-samba-vfs-migration.md#6-umsetzung-in-phasen).
