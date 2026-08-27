# StatusBadge — shared status label

**Status:** Implemented
**Scope:** Web UI (`Kaimo_File_Server.Web`)
**Location:** `Components/Shared/StatusBadge.razor`, `StatusBadge.razor.css`, `StatusTone.cs`

## Purpose

`StatusBadge` is the single component for every place the UI reports the state of an
entity as a small coloured pill: users, shares, client devices, external‑storage
connections and syncs, and so on. Before it existed, each page rendered its own
near‑identical markup and CSS (`status-badge`, `share-availability`, `vs-status`,
`storage-state`, `connection-state`, …). Those differed in dot size, padding,
whether a border was drawn, and which colour meant "attention", so the same
concept looked slightly different on every page.

The badge follows the same pattern as the shared `Tabs`/`Tab` components: one
design lives in one place, and each caller only chooses the semantics that fit
its use case.

## Design

- One pill recipe (border, subtle tinted fill, optional leading dot).
- Four semantic **tones**, each mapped to theme tokens so light and dark are
  handled automatically. The dot inherits the tone colour via `currentColor`.
- Radius `4px` (not a full pill) per the design spec: pill shapes are reserved,
  moderate radii are the norm for controls and indicators.

| Tone | Meaning | Tokens | Example labels |
|---|---|---|---|
| `Positive` | healthy / affirmative | `--success`, `--success-subtle` | Active, Available, Healthy, Ready |
| `Negative` | problem / blocking | `--danger`, `--danger-subtle` | Disabled (user), Revoked, Unavailable |
| `Warning` | needs attention, not broken | `--warning`, `--warning-subtle` | Attention required |
| `Neutral` | informational / off, no judgement | `--border`, `--text-tertiary` | a disabled connection/sync toggle |

The tone drives **only the colour**. The wording is always supplied by the
caller from the language resource files, so the same tone can carry different
localized text on different pages.

### Warning token

The `Warning` tone relies on a `--warning` / `--warning-subtle` token pair that
is now defined for both themes in `wwwroot/css/app.css` (amber, per the design
spec's semantic‑colour rules). Previously `--warning` was referenced by the
external‑storage connection styles but never defined, so the "attention" state
silently fell back to a neutral pill. With the token in place, both the
connection and the sync "attention required" states render as a proper amber
warning.

## API

| Parameter | Type | Default | Notes |
|---|---|---|---|
| `Tone` | `StatusTone` | `Neutral` | Semantic colour. |
| `Label` | `string?` | — | Localized text. Ignored when `ChildContent` is set. |
| `ShowDot` | `bool` | `true` | Set `false` for compact boolean‑style labels (e.g. a Yes/No column). |
| `ChildContent` | `RenderFragment?` | — | Richer content in place of `Label`. |
| `Class` | `string?` | — | Extra classes appended to the pill, for layout hooks. |
| unmatched attributes | — | — | Forwarded to the root `<span>` (e.g. `title`). |

## Usage

```razor
@* Simple, tone chosen from a boolean *@
<StatusBadge Tone="@(user.IsEnabled ? StatusTone.Positive : StatusTone.Negative)"
             Label="@(user.IsEnabled ? Resources.Web_Common_Active : Resources.Web_Common_Disabled)" />

@* Compact boolean pill without a dot *@
<StatusBadge Tone="StatusTone.Positive" ShowDot="false" Label="@Resources.Web_ShareList_Card_Yes" />
```

For a tone with more than two outcomes, compute it in the code‑behind and keep
the markup declarative:

```csharp
private static StatusTone StateTone(StorageConnectionState state) => state switch
{
    StorageConnectionState.Ready    => StatusTone.Positive,
    StorageConnectionState.Disabled => StatusTone.Neutral,
    _                               => StatusTone.Warning,
};
```

### Positioning inside a caller's layout

The badge renders its own root element, so a caller's **scoped** CSS cannot reach
it by class name alone. When a list/grid needs to position the badge (e.g.
`justify-self`, `grid-row`), pass a hook class via `Class` and target it through
`::deep` from an element the caller owns:

```razor
<StatusBadge Tone="@SyncStateTone(item)" Class="sync-state" Label="@SyncState(item)" />
```

```css
.sync-row ::deep .sync-state { justify-self: start; }
```

## Localization

`StatusBadge` introduces no user‑facing strings of its own. Every label continues
to come from `Resources.resx` / `Resources.de.resx` via the existing keys
(`Web_Common_Active`, `Web_ShareList_Card_Available`, `Web_ExternalStorage_Healthy`,
`Web_Devices_Revoked`, …). Adding a new status label means adding the key to both
resource files and passing it to `Label` — the component and its styling stay
untouched.

## Migrated call sites

| Page / component | Previous classes | Tone mapping |
|---|---|---|
| Users list & detail (`AdminEntityList`, `UserDetailsPanel`) | `status-badge`, `status-dot` | enabled → Positive, else → Negative |
| Client devices (`ClientDeviceSettings`) | `status-badge--active/--revoked` | active → Positive, else → Negative |
| Local shares (`ShareCardList`) | `share-availability`, `share-status-dot`, `share-state` | usable → Positive, else → Negative; Yes/No columns use `ShowDot=false` |
| Virtual shares (`CloudAccess`) | `vs-status`, `vs-status-dot` | usable → Positive, else → Negative |
| External‑storage syncs (`SyncAdministration`) | `storage-state`, `storage-state-dot` | healthy → Positive, attention → Warning, disabled → Neutral |
| External‑storage connections (`ConnectionAdministration`) | `connection-state`, `connection-health` | ready → Positive, attention → Warning, disabled → Neutral |

The per‑page CSS blocks listed above were removed. `.connection-health` is kept
because the SSH‑ready and test‑success confirmation lines still use that small dot.
