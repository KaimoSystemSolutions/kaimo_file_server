# Mail notifications

Kaimo File Server sends e-mail notifications for selected events (a user was created, a
file arrived through an upload link, a sync failed, …). Administrators decide which event
sends which mail to whom, edit the mail texts per language and design a shared mail layout.

The original design is in [`mail-notifications-plan.md`](mail-notifications-plan.md); this
document describes what is implemented and how to extend it.

## Pages and permissions

| Where | Permission | Purpose |
|---|---|---|
| **Settings → Mail server** | `ManageMailServer` | SMTP host, port, encryption, login, sender, rate limit; test mail; connection status |
| **Notifications** (`/notifications`) | `ManageNotifications` | Rules, templates, layout, delivery log |

Both bits are granted to the built-in **Administrator** role, but are deliberately **not**
part of `FullAdmin` (which doubles as the "is global admin" test, see `ManageHomes`). The
system role **NotificationManager** carries `ManageNotifications` only. Both pages check the
permission at global scope in their view models and re-check it on every change.

## Architecture

```
 Web / Host / SmbBridge                 PostgreSQL                    Web only
 business action ─► INotificationPublisher.PublishAsync(evt)
                         (best-effort, never throws)
                         ▼
                  notification_events (outbox) ─► NotificationDispatcherService
                  mail_rules ◄──────────────────── 1. claim events (lease)
                  mail_templates / layout ◄──────── 2. match rules, resolve + throttle recipients
                  mail_deliveries ◄──────────────── 3. render one delivery per recipient
                  config: notifications.smtp ◄───── 4. send via MailKit, retry with backoff
```

- **Publishing** (`DbNotificationPublisher`, all processes): writes the event into
  `notification_events`, but only when at least one *enabled* rule exists for its type
  (cached for 30 s). Any failure is logged and swallowed, so a notification problem never
  fails the business action. In read-only demo mode nothing is published.
- **Dispatching** (`NotificationDispatcherService`, Web only, single owner like
  `SearchIndexingService`): only the Web process can decrypt the SMTP password. It polls
  every 10 s and is woken immediately when the Web process itself published.
  - Events are claimed with a 2-minute lease; a failed event is retried up to 5 times.
  - One `mail_deliveries` row per recipient is rendered once and stored (audit + retry).
  - An address receives the mail of one event only once, even if several rules match.
  - Sending respects `MaxMailsPerMinute`. Failures are retried after 1, 2, 4 … minutes
    (capped at 6 h); after 8 attempts, or on a permanent rejection (e.g. unknown
    recipient), the delivery is `Dead` and can be retried manually from the log.
  - While SMTP is disabled or incomplete, deliveries stay `Pending` and the Notifications
    page shows a banner.
  - Finished events and sent/dead deliveries are pruned after 30 days.
- **Default rules**: on start, the dispatcher adds one **disabled** default rule per catalog
  event that has never been seeded (tracked in `notifications.seeded_event_types`). Nothing
  is sent until an administrator enables a rule; deleted default rules are not re-created.

## Event catalog

Defined in code in `Core/Services/Notifications/NotificationEventCatalog.cs`. Typed builders
in `NotificationEvents` keep every call site to one line.

| Event | Raised in | Context roles | Default recipients |
|---|---|---|---|
| `user.created` | `UserListViewModel.CreateUserAsync` | affected, actor | affected user (no password in the mail) |
| `user.password_reset` | `UserListViewModel.SaveUserAsync` | affected, actor | affected user |
| `user.password_changed` | `UserListViewModel.SaveSelfProfileAsync` | affected | affected user |
| `security.account_locked` | `MonitoredLoginService` (once per lock) | affected | affected user + `ViewSecurityMonitor` holders, 60 min throttle |
| `sharelink.upload_received` | `PublicUploadService.UploadAsync` | linkCreator | link creator |
| `sharelink.accessed` | `PublicDownloadController.Download` | linkCreator | link creator, 60 min throttle |
| `sync.failed` | `CloudSyncExecutionService` | syncCreator | sync creator + `SyncAdmin` holders, 6 h throttle |
| `sync.needs_reauthorization` | `CloudSyncExecutionService` | syncCreator | sync creator + `SyncAdmin` holders, 24 h throttle |
| `backup.failed` / `backup.succeeded` | `DatabaseBackupService.CreateBackupAsync` (Host + Web) | – | `ManageBackups` holders |
| `device.registered` | `AuthApiController` (new device only) | affected | device owner |
| `certificate.renewed` / `certificate.renewal_failed` | `CertificateRenewalService` | – | `ManageCertificates` holders |

File-level activity ("new file in share X") is intentionally not part of v1: it is noisy,
has no actor and would need digest mails.

## Recipients

A rule lists any combination of:

- **Context roles** of the event (affected user, triggering user, link creator, sync creator);
- **Users** and **groups** (all enabled members), chosen with `PrincipalPickerField`;
- **Permission holders**: every enabled user holding the permission globally, directly or
  through a group (`ManagementAuthService.GetGlobalPermissionHolderUserIdsAsync`, shared with
  the last-admin guards of the user administration);
- **External addresses** (validated, one plain address each).

Disabled users and users without a valid e-mail address are skipped; the result is
de-duplicated. **Throttling** is per rule, dedup key (e.g. the locked account or the sync)
and recipient.

## Templates and layout

- Templates use **Liquid** (Fluid). Placeholders are dotted names such as
  `{{ user.displayName }}`; the editor lists them per event with sample values. Common
  placeholders: `recipient.name`, `recipient.email`, `server.name` (the SMTP sender name),
  `server.url` (layout "public address", else the default share-link address), `event.time`.
- Built-in defaults for German and English live in
  `Infrastructure/Notifications/DefaultMailTemplates.cs`. A customization is stored in
  `mail_templates` per (event, language); **Reset to default** deletes it, so improved
  defaults reach every installation that has not customized them.
- The **layout** (`notifications.layout`) holds the logo (PNG/JPEG/GIF up to 200 KB, sent as
  an inline CID attachment), the accent color and the header/footer HTML (Liquid).
- All mails are sent in the application language (`app.language`, fallback German). A
  per-user language does not exist yet.

## Adding a new event

1. Add a constant and a `NotificationEventDefinition` (category, context roles, placeholders
   with sample values, default recipients, throttle) to `NotificationEventCatalog`.
2. Add a typed builder to `NotificationEvents` (payload values only, never secrets; set a
   dedup key when the event can repeat).
3. Add built-in templates for **both** languages to `DefaultMailTemplates` (a test fails
   otherwise).
4. Add `Notification_Event_<type>` and `…_Desc` (and a new `Notification_Category_*` if
   needed) to **both** `.resx` files.
5. Call `await publisher.PublishAsync(NotificationEvents.YourEvent(...))` after the business
   action succeeded or failed. Inject `INotificationPublisher?` as an optional parameter so
   existing tests keep constructing the class unchanged.

The dispatcher seeds a disabled default rule for the new event on the next start.

## Security notes

- Event payloads and templates never contain passwords, tokens or link secrets; the welcome
  mail links to the login page only.
- The SMTP password is encrypted with Data Protection (`ICredentialVault`, context
  `WellKnownGUIDs.SMTP_CREDENTIAL`), write-only in the UI and never logged. Server
  certificates are always validated.
- Templates are sandboxed: they only see string dictionaries, loops are bounded, values
  are HTML-encoded and the subject is plain text without line breaks (no header injection).
- Previews render in an `<iframe sandbox>` without scripts. The delivery log body is only
  shown to `ManageNotifications` holders.
- Throttling, the once-per-lock guard and `MaxMailsPerMinute` prevent mail floods, e.g.
  when lockouts are provoked.

## Local testing

`docker-compose.dev.yml` contains **Mailpit**. Configure **Settings → Mail server** with
host `mailpit`, port `1025`, encryption *None* and authentication *None*, send a test mail
and read it at <http://127.0.0.1:8025>.

## Deviations from the plan

- One `INotificationRepository` instead of four repositories (the tables are always used
  together).
- A rule always uses the event's template of the recipient language; named template
  variants per rule (`TemplateId`) are left out until two rules of one event need different
  texts.
- Built-in templates are C# constants instead of embedded `.liquid` files.
- The live preview uses `<iframe sandbox srcdoc>` instead of a `blob:` URL (no JavaScript
  needed); a small helper (`wwwroot/js/mailTemplateEditor.js`) only inserts placeholders at
  the caret.
- The SMTP test mail is sent directly, not through the outbox (`mail.test` event), so the
  result is shown immediately.
- The permission-holder lookup is a static method on `ManagementAuthService`, so the view
  model tests that mock `IManagementAuthService` still exercise the real lookup.
