# Plan: SMTP gateway, event-driven mail notifications and editable mail designs

## Context

Kaimo File Server cannot send e-mail today. There is no SMTP, outbox, event bus or notification code in `src/`, even though `User.Email` already exists and is documented "for notifications or password resets". `docu/public-upload-links-plan.md` already lists "e-mail the creator on upload" as a future item.

Goals:
1. **SMTP gateway**: admins configure an SMTP server login (host, port, TLS, user, password, sender) and send a test mail.
2. **Notification rules page**, gated by its own management permission. Admins define which event sends which mail to whom.
3. **Central event capture**: business actions publish events to one place. A single dispatcher turns them into mails.
4. **Editable mail designs**: a shared layout (logo, accent color, footer) plus per-event templates (subject + HTML) for each language (de/en), with live preview.

Decisions already taken with the user:
- Editor: layout + HTML with `{{placeholders}}`, a code textarea and a sandboxed live preview. No WYSIWYG library.
- Recipients: context recipients (affected user, actor, link creator, sync owner), plus users, groups, permission holders and fixed external addresses.
- Templates exist once per language, with a fallback to the app language.

Constraints found in the codebase. These drive the design and keep it non-invasive.

- **Process placement.** Only **Web** can decrypt secrets: `ICredentialVault`, implemented by `DataProtectionCredentialVault`, is registered only in `Web/Program.cs:200`. Web also already runs the single-owner workers (`SearchIndexingService`, `CloudSyncJobRunner`).
  - **Host** runs scheduled backups.
  - **SmbBridge** has no outbound network: it sits only on internal compose networks.
  - So the design is: every process *publishes* into a DB outbox table, and **Web alone dispatches**. This is the same Web→DB→worker pattern used by `SearchIndexingService` and `DataServiceReconciler`.
- **Pages are gated inside ViewModels**, not with `[Authorize]` policies. They use `IManagementAuthService.GetEffectivePermissionsAtAsync(actor, ScopeType.Global, Guid.Empty)` and expose a `CanAccessPage` flag.
- **New permission bits must not go into `FullAdmin`.** `FullAdmin` doubles as the "is global admin" test. Follow the `ManageHomes` precedent and add new bits only to the seeded Administrator role.
- **Settings persistence** is `config_settings` (a key → JSON blob) via `IConfigRepository`. `BackupSettingsStore` is the reference pattern.

---

## Architecture overview

```
 Web / Host / SmbBridge            PostgreSQL                         Web only
 ───────────────────────           ───────────────────────            ─────────────────────────────
 business action ──► INotificationPublisher.PublishAsync(evt)
                          │  (best-effort, never throws)
                          ▼
                   notification_events (outbox) ──► NotificationDispatcherService (BackgroundService)
                                                     1. claim events (lease)
                   mail_rules  ◄─────────────────────2. match enabled rules for evt.Type
                                                     3. resolve recipients (users/groups/perm/ext)
                   mail_templates / layout ◄─────────4. render template (Fluid) per recipient language
                   mail_deliveries  ◄────────────────5. write one delivery row per recipient
                                                     6. send via MailKit (ISmtpMailSender), retry/backoff
                   config: notifications.smtp ◄──────   (password decrypted with ICredentialVault)
```

New NuGet packages go in `Directory.Packages.props`:
- **MailKit** (MIT) for SMTP. `System.Net.Mail.SmtpClient` is not recommended by Microsoft.
- **Fluid.Core** (MIT) for Liquid templates. It is sandboxed, HTML-encodes by default, and allows no code execution.

---

## Phase 1: SMTP gateway (configuration + test mail)

**Settings model and store** (copy `Infrastructure/Backup/BackupSettings.cs` and `BackupSettingsStore.cs`):
- `Infrastructure/Notifications/SmtpSettings.cs`: a `sealed record` with `ConfigKey = "notifications.smtp"`. Fields:
  - `Enabled`, `Host`, `Port` (default 587)
  - `Security` enum: `Auto | None | StartTls | SslOnConnect`
  - `AuthMode` enum: `None | Password`. OAuth2/XOAUTH2 can come later.
  - `Username`, `EncryptedPassword` (never plaintext)
  - `FromAddress`, `FromName`, `ReplyTo`
  - `TimeoutSeconds`, `MaxMailsPerMinute`
  - `Normalize()`, `Default()`
- `ISmtpConfigStore` / `SmtpConfigStore`: a singleton registered in `Infrastructure/ServiceCollectionExtensions.cs` next to `IBackupSettingsStore` (around lines 115–129). It uses `GetFreshAsync` so it reads across processes.
- Password encryption goes through the existing `ICredentialVault.Protect/Unprotect` with `new CredentialContext(WellKnownGUIDs.SMTP_CREDENTIAL, "smtp", "smtp-password", 1)`.
  - Add `SMTP_CREDENTIAL` to `Core/Helpers/WellKnownGUIDs.cs`.
  - The UI field is write-only: "leave empty to keep the current password".

**Sender:**
- `Core/Services/Notifications/ISmtpMailSender.cs` defines `Task SendAsync(MailMessageModel msg, CancellationToken ct)` and `Task<SmtpTestResult> TestAsync(SmtpSettings draft, string? plainPassword, string to, CancellationToken ct)`.
- `Web/Services/Notifications/MailKitSmtpMailSender.cs` lives in Web because it needs the vault.
  - TLS certificate validation stays ON.
  - It maps MailKit exceptions to user-readable errors, in the style of `ExternalStorageErrorMessage`.

**UI:** a new self-contained Settings tab, following the `ClientDeviceSettings.razor` / `ClientDeviceAdminViewModel` pattern so `SettingsViewModel` does not grow.
- `Web/Components/Pages/Settings/Components/MailServerSettings.razor` (+ `.razor.css`) and `Web/Components/ViewModels/Settings/MailServerSettingsViewModel.cs`, registered as scoped in `Web/Program.cs`.
- Wiring in `Settings.razor` / `Settings.razor.cs`, following the four-place pattern: enum `SettingsTab.MailServer`, a `<Tab>` gated by `VM.CanManageMailServer`, a `case` in the switch, `IsTabPermitted` plus the fallback chain, and a `CanAccessPage` term in `SettingsViewModel.LoadPermissionsAsync`.
- The tab contains:
  - the form, with `InfoButton` hints
  - a **"Send test mail"** button that sends to the current user's e-mail or an entered address, and shows the result as a `StatusBadge`
  - a "Connection status" line showing the last success or error
- Handle `ReadOnlyDemoException` the same way as the other settings sections.

**Permission:** see Phase 5. The tab is gated by `ManageMailServer`.

---

## Phase 2: Central event capture (outbox + publisher)

**Domain** (`Core/Domain/Notifications/`, following the newer entity style: `sealed class`, `…Utc` timestamps, XML docs):
- `NotificationEvent`, the outbox row. Fields:
  - `Id`, `Seq` (bigint identity, gives ordering)
  - `Type` (string key such as `"sharelink.upload_received"`)
  - `OccurredAtUtc`, `ActorUserId?`
  - `SubjectUserIds` (JSON: context-role → userId, e.g. `{"affected": …, "linkCreator": …}`)
  - `PayloadJson` (placeholder values, never secrets)
  - `DedupKey?`, `Status` (`Pending|Processing|Done|Skipped|Failed`)
  - `LeaseUntilUtc`, `AttemptCount`, `LastError`, `ProcessedAtUtc`
- `MailDelivery`, one row per recipient × event. Fields:
  - `Id`, `EventId`, `RuleId`
  - `ToAddress`, `ToUserId?`, `Language`
  - `Subject`, `HtmlBody`, `TextBody`: rendered once and stored for audit and retry
  - `Status` (`Pending|Sending|Sent|Failed|Dead`), `AttemptCount`, `NextAttemptUtc`, `LastError`, `SentAtUtc`

**Event catalog** (in code, not the DB): `Core/Services/Notifications/NotificationEventCatalog.cs`. It holds one `NotificationEventDefinition` per event type with:
- key, resource keys for name and description, category
- available context roles (e.g. `linkCreator`)
- the placeholder schema with sample values (used by the preview)
- default-enabled flag and default recipients
- default throttle

The rules page, the template editor and the placeholder picker are all driven from this catalog. A typed helper per event, e.g. `NotificationEvents.UploadReceived(link, fileName, size, …)`, builds the `NotificationEvent` so call sites stay one line.

**Initial event set and hooks.** Each hook is one additive `await publisher.PublishAsync(...)` after the existing success or failure branch.

| Event key | Hook location | Process | Default recipients |
|---|---|---|---|
| `user.created` | `UserListViewModel.CreateUserAsync()` after `_userRepo.CreateAsync` (~l.1379) | Web | affected user (welcome mail, **no password**) |
| `user.password_reset` | `UserListViewModel.SaveUserAsync()` at `UpdatePasswordAsync` (~l.822) | Web | affected user |
| `user.password_changed` | `UserListViewModel.SaveSelfProfileAsync()` (~l.929) | Web | affected user (security notice) |
| `security.account_locked` | `MonitoredLoginService.AuthenticateAsync` in `Web/Services/SecurityMonitor.cs:238`, when the result is `LockedOut` | Web | affected user + permission holders `ViewSecurityMonitor` |
| `sharelink.upload_received` | `PublicUploadService.UploadAsync` success branch | Web | `linkCreator` |
| `sharelink.accessed` | `PublicDownloadController.Download` after `TryConsumeAccessAsync` | Web | `linkCreator` (disabled by default) |
| `sync.failed` / `sync.needs_reauthorization` | `CloudSyncExecutionService.RunAsync` at `MarkFailedAsync` / `MarkNeedsReauthorizationAsync` | Web | sync creator + `SyncAdmin` holders |
| `backup.failed` / `backup.succeeded` | `DatabaseBackupService.CreateBackupAsync` (success/catch) | Host + Web | `ManageBackups` holders (succeeded disabled by default) |
| `device.registered` | `AuthApiController.ResolveOrCreateDeviceAsync`, create branch only (~l.160) | Web | device owner |
| `certificate.renewed` / `certificate.renewal_failed` | `CertificateRenewalService` | Web | `ManageCertificates` holders |
| `mail.test` | Test button (Phase 1/4) | Web | explicit address |

File-level activity ("new file in share X"), driven by tailing `file_change_log` like `SearchIndexingService`, is **deliberately left out of v1**. It is noisy, it has no actor, and it needs digesting. It is noted as a follow-up.

**Publisher:**
- `Core/Services/Notifications/INotificationPublisher.cs` and `Infrastructure/Notifications/DbNotificationPublisher.cs`.
- It is registered in `AddInfrastructure`, so Web, Host and SmbBridge all get it.
- It is **best-effort**: it catches and logs every exception, the same way `FileService.AppendChangeAsync` does, so a DB hiccup never fails the business action.
- It skips the insert cheaply when no enabled rule exists for the type, using a cached rule-type set with a short TTL, so the outbox is not filled with events nobody cares about.
- Constructor injection is additive: optional parameters where existing tests construct the classes directly (e.g. `UserListViewModel` test harness, `CloudSyncExecutionService` tests). This keeps the test setup untouched.

---

## Phase 3: Dispatcher, rules and recipient resolution

**Rules:**
- `Core/Domain/Notifications/MailRule.cs` has these fields:
  - `Id`, `EventType`, `Name`, `Enabled`
  - `RecipientsJson`: a list of `RecipientSpec { Kind: ContextRole|User|Group|PermissionHolder|ExternalAddress, Value }`
  - `TemplateId?` (null = the event's default template)
  - `ThrottleMinutes` (per `DedupKey` + recipient)
  - `CreatedAtUtc`, `UpdatedAtUtc`, `UpdatedByUserId`
- Several rules may exist per event, e.g. "welcome mail to user" plus "info to admins".
- On first Web start, when the table is empty, the catalog seeds **disabled** default rules. Nothing is sent until an admin enables it and SMTP is configured.

**Recipient resolver:** `Infrastructure/Notifications/NotificationRecipientResolver.cs`. It reuses existing lookups:
- users: `IUserRepository.GetByIdAsync`
- groups: `IGroupRepository.GetMembersAsync`
- role holders: `IScopedRoleAssignmentRepository.GetByScopeAsync(ScopeType.Global, Guid.Empty)` plus group expansion
- **Permission holders.** Move the private `UserListViewModel.GetEnabledGlobalAdminUserIdsAsync()` (~l.1746) into `ManagementAuthService` as `GetGlobalPermissionHolderUserIdsAsync(ManagementPermission mask)`. The comment there already asks for this once a second caller exists. `UserListViewModel` then calls the shared method.

The resolver also:
- skips disabled users and users without an e-mail
- validates external addresses with `MailboxAddress.TryParse`
- de-duplicates recipients
- picks the language from the app language (`app.language`) and falls back to `de`. A per-user language setting does not exist today and would be a follow-up.

**Dispatcher:** `Web/Services/Notifications/NotificationDispatcherService.cs : BackgroundService`, registered only in `Web/Program.cs` next to `SearchIndexingService`, with the same single-owner comment.
- The loop polls every few seconds and is woken early by an in-process signal (like `CloudSyncSchedulerSignal`) when Web itself published.
  1. Claim `Pending` events with a lease, copying the lease/attempt pattern of `SambaLifecycleEventRepository.TryClaimAsync` (`ExecuteUpdateAsync` + `LeaseUntilUtc`).
  2. Match rules, resolve recipients and apply the throttle.
  3. Render and insert `MailDelivery` rows. Mark the event `Done`, or `Skipped` when no rule or recipient applies.
  4. Send due deliveries through `ISmtpMailSender`, respecting `MaxMailsPerMinute`. Use exponential backoff (1 min → 6 h, max ~8 attempts, then `Dead`).
- If SMTP is disabled or unconfigured, deliveries stay `Pending` and the page shows a banner.
- Retention: prune `Done`/`Skipped` events and `Sent`/`Dead` deliveries after 30 days, in the same loop, in the style of `ClientSyncRetentionService`.

**Repositories** (interfaces in `Core/Repositories/`, implementations in `Infrastructure/Repositories/`, registered scoped in `AddInfrastructure`, using `IDbContextFactory` with short-lived contexts):
- `INotificationEventRepository`
- `IMailDeliveryRepository`
- `IMailRuleRepository`
- `IMailTemplateRepository`

**EF:**
- Add `DbSet`s and `modelBuilder.Entity<…>` blocks in `Infrastructure/Persistence/ApplicationDbContext.cs` for the tables `notification_events`, `mail_deliveries`, `mail_rules`, `mail_templates`.
- Indexes: `(Status, Seq)`, `(Status, NextAttemptUtc)`, `(EventType)`, and a unique index `(EventType, Language, Name)`.
- Enums use `HasConversion<int>()`, JSON goes in `text` columns.
- One migration, `yyyyMMddHHmmss_AddMailNotifications`, generated with `dotnet ef migrations add` in the Infrastructure project. It is applied by the Host as usual.

---

## Phase 4: Editable mail designs (layout + templates, per language)

**Model:**
- `Core/Domain/Notifications/MailTemplate.cs` has these fields:
  - `Id`, `EventType`, `Language` (`de`/`en`), `Name`
  - `SubjectTemplate`, `HtmlTemplate`, `TextTemplate?` (auto-generated from the HTML when empty)
  - `IsDefault`, `UpdatedAtUtc`, `UpdatedByUserId`
- **Layout:** a config key `notifications.layout` with a JSON record `MailLayoutSettings`. Fields: `LogoDataUri?` (size-capped, e.g. 200 KB, sent as an inline CID attachment), `AccentColor` (defaults to the Kaimo green token), `HeaderHtml`, `FooterHtml` (Liquid), and `PublicBaseUrl` (for links in mails; defaults to the default base address in `ShareLinkSettings`).
- **Built-in defaults** are embedded resources in `Infrastructure/Notifications/DefaultTemplates/{event}.{lang}.liquid` plus `layout.liquid`. They are used whenever no customized DB row exists. **"Reset to default"** deletes the customized row. This way default templates can improve with updates without migrations.

**Renderer:** `Infrastructure/Notifications/MailTemplateRenderer.cs` (Fluid).
- It uses a parsed-template cache.
- A strict `TemplateOptions` exposes only the event payload plus `user`, `server`, `link` and `layout`. There is no member access to arbitrary .NET objects.
- Output is HTML-encoded by default. The subject is rendered as plain text, and CR/LF are stripped against header injection.
- The body is wrapped into the layout via `{{ content }}`.
- A simple HTML→text fallback produces the plain-text part.
- `ValidateAsync` returns parse errors with line and column for the editor.

**Page `/notifications`** (new page gated by `ManageNotifications`):
- Files: `Web/Components/Pages/Notifications/Notifications.razor` (+ `.razor.css`), `Web/Components/ViewModels/Notifications/NotificationsViewModel.cs` (scoped), and `MailTemplateEditorViewModel.cs`.
- It uses the horizontal `<Tabs>` / `<Tab>` from `Components/Shared`, `StatusBadge`, `InfoButton`, `PrincipalPickerField` (for users/groups) and `MultiSelect`.

The page has four tabs:
1. **Rules ("What happens on which action").**
   - A list grouped by catalog category: event, enabled toggle, recipients summary, template and last sent.
   - The detail panel (list + detail split, per `DESIGN_SPEC.md`) edits the recipients, template choice and throttle.
2. **Templates.**
   - A list of event × language. The editor has a subject input and an HTML `textarea` (monospace).
   - A placeholder side panel from the catalog; clicking inserts `{{ … }}`.
   - A **live preview** rendered with the catalog's sample payload in an `<iframe sandbox>` loaded from a `blob:` URL. The CSP already allows `frame-src 'self' blob:`, see `SecurityHeadersMiddleware.cs:48`. A small JS helper sits next to `wwwroot/js/filePreview.js`.
   - Buttons: validation errors inline, "Send test to me", "Reset to default".
3. **Layout.** Logo upload (reusing the existing avatar/photo upload handling), accent color, header/footer HTML, and a preview of a sample mail.
4. **Delivery log.**
   - Recent `MailDelivery` rows with status, recipient, event, error and attempts.
   - Actions: retry a `Dead` delivery, view the rendered mail.
   - The body is shown only to `ManageNotifications` holders, because it contains user data.

Navigation and search:
- In `Web/Components/Layout/MainLayout.razor`, add `_canManageNotifications` via `ManagementAuthService.HasAnyPermissionAsync(_currentUser, ManagementPermission.ManageNotifications)` and a `NavLink href="/notifications"`. A new `wwwroot/svg/mail.svg` icon keeps the existing icon style.
- In `Web/Components/Search/SearchDestinations.cs`, add entries for `/notifications` and `/settings?tab=mailserver`.

---

## Phase 5: Permissions, localization, docs

**`Core/Security/ManagementPermission.cs`** gets two new bits in the "System" block:
```csharp
ManageMailServer    = 1L << 46,  // configure the SMTP gateway, send test mails
ManageNotifications = 1L << 47,  // edit notification rules, mail templates/layout, view delivery log
```
- These are **not** added to `FullAdmin` or `SystemAdmin`, following the `ManageHomes` precedent.
- In `Infrastructure/Persistence/DatabaseSeeder.cs` (`RoleDefinitions`):
  - Administrator becomes `FullAdmin | ManageHomes | ManageMailServer | ManageNotifications`.
  - Add a new system role `("NotificationManager", ManageNotifications, true, WellKnownGUIDs.ROLE_NOTIFICATION_MANAGER)` with GUID `…100000000009`.
- There is no schema change for the bitmask. System roles converge through the seeder on Host start.
- In `UserListViewModel.PermissionGroups` (~l.379–450), add both bits to the System group so they appear in `RoleDetailsPanel.razor`.
- Mail-server changes additionally re-check the permission inside the ViewModel save method. Do not copy the missing re-check in `ShareLinkService.SaveSettingsAsync`.

**Localization:** add all keys (`Web_Nav_Notifications`, `Web_Perm_ManageMailServer`, `Web_Perm_ManageNotifications`, `Web_Settings_Tab_MailServer`, `Web_Notifications_*`, and event names/descriptions `Notification_Event_*`) to `Core/Language/Resources.resx` and `Resources.de.resx`. Access them via the runtime `R("key")` style, which needs no Designer regeneration.

**Docs:**
- `docu/mail-notifications.md`, in the style of `docu/public-upload-links-plan.md`: architecture, event catalog, how to add a new event (catalog entry, default template × 2 languages, one `PublishAsync` call), and security notes.
- Mention SMTP in `README.md` features and `example/` (outbound port 587/465 needed from the `kaimo_file_server.web` container).

---

## Security notes

- Never put passwords, tokens, share-link secrets or reset tokens in event payloads or templates. The welcome mail links to the login page only.
- The SMTP password is encrypted with Data Protection, write-only in the UI, and never logged.
- Templates are sandboxed (Fluid), HTML-encode by default, and have a header-injection guard on the subject.
- The preview iframe is `sandbox` without `allow-scripts`.
- External recipient addresses are only configurable by `ManageNotifications` holders. Throttling and `MaxMailsPerMinute` prevent mail floods, e.g. when lockouts are attacked.
- Demo mode is read-only (`ReadOnlyDemoSaveInterceptor`), so no mails are sent there. The dispatcher also checks the demo flag.

---

## Implementation order (each step builds and tests green on its own)

1. Packages, `SmtpSettings` + store, `MailKitSmtpMailSender`, permission bits, seeder, Settings tab with test mail.
2. Domain entities, EF config, migration, repositories, publisher, event catalog.
3. Dispatcher, recipient resolver (including the `ManagementAuthService` extraction), default rules + templates, renderer.
4. Event hooks (one small commit per area).
5. `/notifications` page: rules tab, template editor + preview, layout tab, delivery log, nav + search entries.
6. Localization, docs.

---

## Verification

**Unit / DB tests** in `tests/Kaimo_File_Server.Tests`, using `DatabaseTestBase` (SQLite, real schema) and Moq, as in `VIEWMODEL_TESTING.md`:
- `SmtpConfigStoreTests`: normalize, round-trip, and that the password is stored encrypted and never in plaintext.
- `MailTemplateRendererTests`: placeholders, HTML-encoding of payload values, CR/LF stripped from the subject, layout wrapping, language fallback, parse errors reported.
- `NotificationPublisherTests`: best-effort (a throwing repo doesn't throw), skip when no rule.
- `NotificationDispatcherTests`: claim/lease, rule matching, dedupe/throttle, retry backoff → `Dead`, SMTP disabled keeps `Pending`. Uses a fake `ISmtpMailSender`.
- `NotificationRecipientResolverTests`: users, groups, permission holders, disabled/no-email users skipped, dedupe.
- Extend `DatabaseSeederTests` (Administrator has both new bits, NotificationManager role exists), `ManagementPermissionLifecycleDatabaseTests`, and `UserListViewModelDatabaseTests` (the refactored admin lookup behaves the same).
- ViewModel tests for `MailServerSettingsViewModel` / `NotificationsViewModel` permission gating (no permission → `CanAccessPage == false`, save rejected).

**Commands:** `dotnet build Kaimo_File_Server.slnx` and `dotnet test tests/Kaimo_File_Server.Tests`. The same checks run in `.github/workflows/tests.yml`.

**End-to-end (manual):**
1. Add a local SMTP catcher to `docker-compose.dev.yml`, e.g. `axllent/mailpit` on `127.0.0.1:8025`, and start the stack.
2. Configure SMTP in Settings → Mail server, then send a test mail. It appears in Mailpit.
3. Enable the `sharelink.upload_received` rule, upload a file via a public upload link, and check that the creator receives a mail rendered with the custom layout.
4. Edit the template, check the live preview, and reset to default.
5. Stop Mailpit and trigger an event. The delivery shows `Failed` with a retry. Restart Mailpit and the delivery goes to `Sent`.
6. Log in as a user without `ManageNotifications`. The nav entry and page are hidden and the page shows the no-permission banner.
