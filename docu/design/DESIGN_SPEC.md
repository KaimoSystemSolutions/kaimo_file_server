# Kaimo Files – Design Specification

**Status:** Binding visual and conceptual baseline, aligned with the implemented Web UI as of 2026-09-27
**Scope:** Web UI (`Kaimo_File_Server.Web`) and the Flutter client apps (desktop, iOS, Android)
**Source of truth:** `src/Kaimo_File_Server.Web/wwwroot/css/app.css` (tokens and global primitives) and `src/Kaimo_File_Server.Web/Components/Shared/` (shared components). This document describes them; if the two ever disagree, fix whichever is wrong and keep both in sync.
**Level of detail:** System-level spec. Individual pages and workflows are specified separately (e.g. `status-badge-component.md`).

## 1. Summary for AI systems

Build Kaimo Files as a focused, robust file-server workbench with strong orientation and visible functions. The interaction model follows the strengths of classic Windows work surfaces — persistent navigation, clear zones, visible commands, lists, tree views, toolbars, detail panels and status indicators — reinterpreted with modern typography, crisp shapes, clean spacing and accessible interaction. It is not a retro skin.

The UI is predominantly neutral, built from several clearly distinguishable neutral layers in both dark and light mode. One accent color — user-selectable, green by default — is used sparingly for primary actions, focus, selection and brand. Shapes are crisp (2–4 px radii). All styling comes from the Kaimo tokens and classes in `app.css` and the shared Blazor components; there is no framework default look anywhere, and Bootstrap is not loaded.

## 2. Normative keywords

- **MUST / MUST NOT:** mandatory.
- **SHOULD:** the default decision; deviations need a documented functional reason.
- **MAY:** allowed option, not the default.

Page specs may extend these rules but must not silently override them.

## 3. Design identity

### 3.1 Intended impression

Kaimo Files MUST feel like a focused tool that people trust with their data and infrastructure:

- clear, direct and dependable;
- functionally rich without visual clutter;
- technically competent but not cold;
- distinctive and recognizable, but not decorative or playful;
- efficient for enthusiasts and professionals, still readable for less experienced users.

The personality MAY subtly evoke a turtle — calm, robust, long-lived, protected — through stable forms and a calm look, not through a pervasive animal motif.

### 3.2 Modernized Windows principles

- Functions are visible where they are needed.
- Primary commands have text labels or unambiguous, well-known icons.
- Navigation, content, properties and status are recognizable as separate zones.
- Tables/lists, tree views, split panes (list + detail), toolbars and status indicators are first-class components.
- Right-click, hover and keyboard shortcuts speed up work but are never the only way to reach an important function.
- A selection produces visible, contextual commands and information (selection toolbar, detail panel).
- Information density is organized, not blanket-reduced.

Not meant: pixelated retro graphics, heavy 3D bevels, grey 1990s dialogs, imitated OS chrome or a copy of Windows Explorer.

## 4. Core principles

### 4.1 Visible over hidden

- Frequent and important actions MUST be reachable without a context menu, hover reveal or nested overflow menu.
- Context menus SHOULD offer the same commands as a faster secondary path.
- Icon-only buttons are allowed only for universally known commands or tight toolbars and MUST have a tooltip and an accessible name.
- Progress, selection, filters, sorting, permission scope and active synchronizations MUST be communicated visibly (e.g. running-jobs menu, progress toasts, `StatusBadge`).

### 4.2 Progressive disclosure without loss of function

Not everything is shown at once, but the UI MUST signal that more exists. Preferred means: tabs (`<Tabs>`), collapsible sections, detail panels, split views, `InfoButton` explanations and dialogs for focused tasks.

### 4.3 Structure before decoration

Hierarchy comes first from position, spacing, typography, dividers and distinct neutral surfaces; color and shadow are secondary. No hero areas or marketing layouts inside the signed-in application.

### 4.4 Calm density

The default is a balanced-to-compact workbench (base font 14 px, most UI text 12–13 px, list rows ≈ 40 px). Admin create/edit forms MAY opt into the denser `.admin-compact` variant. Avoid large empty cards, oversized headings and needlessly tall controls.

## 5. Color system

### 5.1 Ground rule

At least ~85 % of the perceived surface SHOULD be neutral. The accent is reserved for brand, primary action, active selection, focus and "active/changed" indicators. It MUST NOT be used as a general fill for icons, file types, navigation areas, table rows or headings.

Other colors are allowed only for real semantics: red for danger/error, amber for warning, blue for neutral information, plus the fixed entity and folder colors (5.5). They must not act as competing brand colors.

### 5.2 Theming model

- The theme is set by `data-theme="dark" | "light"` on the root element. **Dark is the default.**
- The accent is set independently by `data-accent` on the same element. Available accents (`ThemeService.Accents`): **green (default)**, magenta, orange, red, blue, yellow, turquoise. Green has no override block; the other accents override only the accent tokens per theme.
- Both values are persisted in `localStorage` (`kaimo_theme`, `kaimo_accent`) and applied by a head script in `App.razor` before first paint. Switching is a pure attribute flip.
- Consequence: code MUST reference accent tokens, never a concrete accent value. Anything built for "green" must also work with every other accent in both themes.

### 5.3 Accent tokens (default green)

| Token | Dark | Light | Use |
|---|---:|---:|---|
| `--color-accent-brand` / `--accent-brand` | `#4AAD0A` | `#3D9400` | logo, brand details |
| `--color-accent-action` / `--accent` | `#3F9E00` | `#338000` | primary buttons, focus ring, active indicators |
| `--color-accent-hover` / `--accent-hover` | `#57BD12` | `#2B6D00` | hover of accent elements |
| `--color-accent-active` | `#338000` | `#235A00` | pressed state |
| `--color-accent-soft` / `--accent-subtle` | `#1D3311` | `#E3F3D1` | active nav item, focus halo, soft selection |
| `--color-accent-border` / `--accent-border` | `#3D7A1E` | `#A3CE7A` | accent edges, active nav border |
| `--accent-strong` | `#6FCE30` | `#235A00` | high-emphasis accent text (selected row name, avatar initials) |
| `--accent-selected` | `#223F13` | `#D5ECBD` | selected list row / card background |
| `--accent-rgb` | `63, 158, 0` | `51, 128, 0` | for `rgba(var(--accent-rgb), a)` |
| `--text-on-accent` | `#FFFFFF` | `#FFFFFF` | text on accent fills |
| `--avatar-ring` | `#86EFAC` | `#166534` | profile-picture ring |

Color MUST never be the only carrier of information.

### 5.4 Neutral surfaces

Surfaces differ deliberately in lightness so dark and light mode never look "all the same".

| Semantic token | Legacy alias | Dark | Light | Typical use |
|---|---|---:|---:|---|
| `--color-canvas` | `--bg-primary` | `#151816` | `#E7EAE5` | page background, input fill |
| `--color-surface` | `--bg-secondary` | `#1E221F` | `#FFFFFF` | panels, lists, dialogs, cards |
| `--color-surface-raised` | `--bg-tertiary` | `#272C28` | `#F3F6F1` | sidebar, list headers, secondary buttons, sticky search bar |
| `--color-surface-sunken` | `--bg-sunken` | `#111411` | `#DBDFDA` | recessed areas |
| `--color-surface-hover` | `--bg-hover` | `#303630` | `#E5EAE3` | hover |
| — | `--bg-active` | `#384039` | `#D2D7D1` | pressed / toggle off track |
| `--color-border` | `--border` | `#3B433C` | `#BBC2BA` | default borders, dividers |
| — | `--border-light` | `#465047` | `#CCD1CB` | inner/secondary borders |
| `--color-border-strong` | `--border-strong` | `#59625B` | `#8C948D` | input borders, emphasis |
| `--color-text` | `--text-primary` | `#F0F3F0` | `#1B201C` | primary text |
| `--color-text-secondary` | `--text-secondary` | `#BCC3BD` | `#505852` | secondary text |
| `--color-text-muted` | `--text-tertiary` | `#929B94` | `#707872` | metadata, micro-labels, empty states |

Both naming sets are valid; the short legacy aliases (`--bg-*`, `--text-*`, `--border*`, `--accent*`) are what most existing CSS uses and SHOULD be used for consistency. Canvas, sidebar, content surface, list header and dialog MUST stay distinguishable in greyscale; separation must not rely on shadow alone.

### 5.5 Semantic, entity and folder colors

| Token (+ `-subtle`/`-hover`) | Dark | Light | Meaning |
|---|---:|---:|---|
| `--success` / `--success-subtle` | `#4ADE80` | `#16A34A` | success, positive state |
| `--warning` / `--warning-subtle` | `#FBBF24` | `#B45309` | warning, attention |
| `--danger` / `--danger-subtle` / `--danger-hover` | `#F87171` | `#DC2626` | error, destructive action |
| `--info` | `#7BB7E8` | `#256B9B` | neutral information, progress |
| `--entity-user` / `-soft` | `#77BCE8` | `#236F9F` | users |
| `--entity-group` / `-soft` | `#B59AE7` | `#6F4EAA` | groups |
| `--entity-role` / `-soft` | `#E2B769` | `#986313` | roles |
| `--entity-department` / `-soft` | `#E28A84` | `#A3453F` | departments |
| `--folder-fill` / `--folder-stroke` | amber | amber | folder icons |

Semantic backgrounds are tinted (`*-subtle`); solid fills are reserved for badges, small indicators and important actions. Entity colors identify principal types consistently (picker, chips, tree, lists) and are independent of the chosen accent. Disabled is neutral with `opacity: 0.5` — never faded to illegibility.

## 6. Typography

- **Typeface:** Plus Jakarta Sans (self-hosted variable font, `wwwroot/fonts/`) is the Kaimo typeface. It is exposed as `--font-heading` and used for headings, brand text and micro-labels. Body text and controls SHOULD use it as well (see §18 #1 for the current gap).
- Base: `14px`, `line-height: 1.5`, antialiased.

| Role | Size / weight | Notes |
|---|---|---|
| Micro-label (section, column, group) | 11 px / 600–700, uppercase, `--label-spacing` | `.ui-label`, `.detail-label`, `.file-list-header`; `--text-tertiary` |
| Metadata, hints, badges | 12 px | `.form-hint`, `.detail-panel-subtitle`, `StatusBadge` (11 px) |
| Standard UI text, buttons, rows, nav | 13–13.5 px / 500–600 | `.btn*`, `.file-cell`, `.nav-item` |
| Body, inputs | 14 px | `.edit-input`, `.modal-input` (13 px) |
| Dialog title | 15 px / 600 | `.modal-title` |
| Panel name | 16 px / 700 | `.detail-panel-name` |
| Brand text | 18 px / 680 | sidebar |
| Page title | 22 px / 700, `-0.02em` | `.page-header h1` (18 px below 480 px) |

- Headings are compact and factual, never marketing headlines.
- Paths, hashes, ports, IDs and logs MAY use monospace (`.detail-value--mono`).
- Weight and size create hierarchy; the accent MUST NOT replace typographic hierarchy.
- File names and primary object names outrank supporting metadata.

## 7. Shape, spacing and depth

### 7.1 Radii — "crisp"

| Token | Value | Applies to |
|---|---:|---|
| `--radius-control` | 2 px | buttons, inputs, selects, chips, badges, nav items, toggles' inner parts |
| `--radius-surface` | 3 px | cards, panels, list wrappers, dropdowns, menus, toasts |
| `--radius-modal` | 4 px | modal dialogs |

- Literal `50%` is allowed only for avatars, status dots and spinners; `999px` only for small round swatches/indicators.
- No other literal radius values. Pill shapes are not used for buttons or badges (`StatusBadge` uses `--radius-control`).
- The whole UI can be switched to square (all three = 0) or "mixed" by changing only these tokens — keep it that way.

### 7.2 Spacing

4 px base scale: `--space-1` 4 · `--space-2` 8 · `--space-3` 12 · `--space-4` 16 · `--space-5` 24 · `--space-6` 32. New CSS SHOULD use these tokens; common literal values in existing CSS follow the same grid (8/12/16/20/24).

### 7.3 Depth

- `--shadow` for lightly lifted elements, `--shadow-lg` for dialogs, context menus, popovers and toasts only.
- Permanently adjacent zones (sidebar, list, detail panel) are separated by surfaces and 1 px borders, not floating shadowed cards.
- A screen MUST NOT dissolve into a collection of identical floating cards. Card grids are acceptable only where the content is genuinely a set of objects (e.g. shares) and then use `--bg-secondary` + `--border` + `--radius-surface`, no shadow.
- Dividers are a deliberate tool: list rows, toolbars, panel headers and action zones use `1px solid var(--border)`.

## 8. Icons and imagery

- Icons are inline SVGs from `wwwroot/svg/` (stroke-based, `currentColor`), so they inherit text color and theme. Nav icons are 18 px; toolbar/row icons 14–18 px.
- Icons are concrete and quickly recognizable; avoid abstract symbols.
- File types have dedicated icons (`file_pdf.svg`, `file_word.svg`, …) with restrained own colors; folders use `--folder-fill` / `--folder-stroke`. They are never recolored with the accent.
- External storage providers (SMB, SFTP, rsync, WebDAV, OneDrive, Google Drive, Dropbox) have their own icons and fixed, theme-independent identification colors (`--provider-*` tokens in `:root`).
- Primary toolbar actions SHOULD combine icon and text; compact secondary actions MAY be icon-only (`.btn-icon`, 34/30 px) with tooltip and `aria-label`.
- The turtle motif MAY appear in logo, onboarding or selected empty states, never as decoration on every view.

## 9. Layout and navigation

### 9.1 Application shell (`MainLayout`)

- **Sidebar** (248 px, `--bg-tertiary`, right border): brand at the top, vertical `nav-item` list, footer on `--bg-secondary` with user avatar/name (links to own profile), logout, theme toggle and accent picker.
- **Active nav item:** `--accent-subtle` background, `--accent-border` border, a 3 px accent bar on the left edge and heavier text. Hover: `--bg-hover` + border. Never a solid accent block.
- **Main area** (`.main`): scrolls independently, padding `0 32px 24px`. The first child is the sticky global search bar on `--bg-tertiary` (controls share `--topbar-control-h: 40px`, including the scope select and the running-jobs menu).
- **Page header** (`.page-header`): title (`h1`) + `.subtitle` on the left, page actions on the right, 24 px bottom margin.

### 9.2 Work areas

- Information-rich areas use **lists** (header row + `file-row`-style rows) as the default.
- **Admin pattern** (users, groups, roles, departments, shares, connections): `.admin-content` = list on the left, sticky `.admin-detail` panel (≈ 450–460 px) on the right. Selecting a row fills the panel; with no selection it shows `.detail-empty`.
- Detail panels: `.detail-panel-header` (icon tile + name + subtitle), `<Tabs Variant panel>` for sections, read-only fields as `.detail-field-grid` / `.detail-field-value` (visually distinct from edit inputs), actions in `.detail-actions` separated by a top border.
- Hierarchical data (folders, departments, groups) SHOULD use a real tree view with indentation and expand/collapse state.
- Multi-selection shows the `.selection-toolbar` (count + contextual `.toolbar-btn` actions) above the list.
- Search, filters and sorting MUST show their active state.
- **Selected rows/cards:** `--accent-selected` background + 3 px accent left border, name in `--accent-strong`. Never a strong solid accent fill.

### 9.3 Responsive behavior

Responsiveness means prioritization, not shrinking:

| Width | Behavior |
|---|---|
| > 1100 px | full sidebar, list + detail side by side |
| 769–1100 px | sidebar collapses to a 72 px icon rail (labels and user name hidden) |
| ≤ 768 px | sidebar becomes a 60 px top bar; main padding 16 px; list and detail stack; selection toolbar stacks; less important columns (date) hide |
| ≤ 480 px | further columns (size) hide; page title 18 px |

Component-level breakpoints around 620 px are used for dense forms. Important actions stay directly reachable; context menus stay within the viewport. Touch targets grow on small screens without making the desktop view coarse.

### 9.4 Client apps

Web, desktop and mobile share tokens, typography principles, icons, states and brand identity (the Flutter app mirrors these tokens via `ThemeData` + `ThemeExtension`, see `docu/client-sync-api/`). Navigation and interaction patterns SHOULD adapt to each platform: a desktop toolbar is not copied unchanged onto a phone, and mobile bottom navigation is not forced onto desktop.

## 10. Component inventory

Reuse these before writing anything new. New patterns that will be needed more than once belong in `Components/Shared/` (component + scoped CSS) or, for pure CSS primitives, in `app.css`.

### 10.1 Shared Blazor components (`Components/Shared/`)

| Component | Purpose |
|---|---|
| `Tabs` / `Tab` | All tab strips. Variants: horizontal underline (default), vertical (settings nav), `detail-tabs--panel` (full-bleed bar inside detail panes). Active = accent underline/indicator. |
| `MultiSelect<T>` | Compact checkbox dropdown for small option sets (e.g. context-menu settings). |
| `PrincipalPickerField` | Single/multi selection of users, groups, roles, departments, shares: modal with search, type tabs, scrollable list, selected chips. Always use it for principal selection. |
| `StatusBadge` | Every entity status pill; `Tone` = Positive/Negative/Warning/Neutral. See `status-badge-component.md`. |
| `InfoButton` | Inline "i" button that reveals an explanation (e.g. password rules) instead of static hint walls. |

### 10.2 Global CSS primitives (`app.css`)

| Area | Classes |
|---|---|
| Buttons | `.btn-primary` (accent fill, one per command group), `.btn-secondary` (neutral, bordered), `.btn-danger`, `.btn-danger-outline` (tinted, fills on hover), `.btn-secondary--sm`; all 8×16 px, 13 px; disabled = 50 % opacity + `not-allowed` |
| Icon buttons | `.btn-icon` (34 px, accent hover), `.btn-icon--small` (30 px, neutral hover), `.btn-icon--danger` |
| Toolbars | `.selection-toolbar`, `.toolbar-left`, `.toolbar-count`, `.toolbar-actions`, `.toolbar-btn`, `.toolbar-btn--danger` |
| Forms | `.form-group` (label + control), `.edit-input` (inputs and `select.edit-input` with custom chevron), `.form-hint`, `.toggle-row` + `.toggle-switch`/`.toggle-slider` for booleans, styled file picker; checkboxes/radios use `accent-color: var(--accent)`; `.admin-compact` for dense admin forms |
| Focus | Global `:focus-visible` = 2 px accent outline, 2 px offset; text fields add a 3 px `--accent-subtle` halo |
| Lists | `.file-list-header` (micro-label row on `--bg-tertiary`), `.file-row` (10×16 px, bottom border, hover `--bg-hover`), `.file-row--selected` / `.admin-row-selected` |
| Detail panel | `.admin-content`, `.admin-list`, `.admin-detail`, `.detail-empty`, `.detail-panel-header`, `.detail-panel-name`, `.detail-panel-subtitle`, `.detail-label`, `.detail-value(--mono)`, `.detail-field-grid`, `.detail-field-value`, `.detail-info` (dashed hint box), `.detail-actions` |
| Create forms | `.create-form-card`, `.create-form-fields`, `.create-form-toggles`, `.create-form-actions` |
| Dialogs | `.modal-backdrop` (45 % black), `.modal-dialog` (`--bg-secondary`, border, `--radius-modal`, 24 px padding, `--shadow-lg`), `.modal-title`, `.modal-text`, `.modal-input`, `.modal-error`, `.modal-warning`, `.modal-actions` (right-aligned, cancel then primary) |
| Menus | `.context-menu` (surface, border, `--shadow-lg`, 4 px padding, 13 px items, danger items tinted red), `.multi-select*` |
| Feedback | `.error-banner`, `.warning-banner`, `.success-banner` (tinted, bordered, 13 px); toasts (`ToastContainer`: success/error/info/progress with progress bar, bottom-right); running-jobs menu in the top bar |
| States | `.loading-state` + `.spinner`, skeleton loaders for slow share loads, `.empty-state` / `.empty-hint`, `.detail-empty`, themed Blazor reconnect dialog |
| Settings | stat hero, grouped read-only rows, usage bars |

### 10.3 Behavioral rules

- **Buttons:** at most one visually dominant primary action per local command group. Destructive actions are red, never accent. Every button defines default, hover, focus-visible and disabled; long-running actions show a spinner or progress.
- **Lists:** header, row, hover and selection are distinct states. Row actions MUST NOT appear only on hover. Sorting is shown in the header.
- **Tabs** look like navigation within an area and must not be confused with buttons. The legacy segmented `.admin-tabs` MUST NOT be used for new work — use `<Tabs>`.
- **Read vs. edit:** read-only values render as `.detail-field-value`, never as disabled inputs, so view mode is never confused with edit mode.
- **Dialogs** have a clear title, structured content and a stable action zone. Long settings are split into tabs or sections instead of one huge form. Frequently consulted properties belong in the detail panel; rare, focused tasks may be dialogs.
- **Toggles vs. checkboxes:** boolean settings that take effect as a setting use the toggle switch; checkboxes are for selection in lists and permission grids.
- **System states:** loading, empty, error, disabled, permission denied, partial success, read-only demo mode (`demo-banner`) and background activity MUST be designed as their own states. Toasts must not replace persistent information; long uploads, downloads and syncs need visible progress that can be found again (running-jobs menu).

## 11. Dark and light mode

- Both themes are equal products, not automatic inversions. Dark is the default; the user can switch via the sidebar toggle.
- Each mode has several neutral layers with sufficient contrast (see 5.4).
- Dark mode uses no pure black as dominant background and no pure white for large text; light mode is not "all white" — canvas, sidebar, list headers and inputs are tinted.
- Accents are lighter in dark mode to keep meaning and contrast.
- Every new element MUST be checked in both themes and with at least one non-green accent.

## 12. Motion and interaction

- Transitions are short and direct: 0.1–0.2 s on background, border and color (`120ms ease` in navigation). Panels and toolbars MAY fade in (`fadeIn` 0.15 s). No springy or playful motion.
- Spinners use the shared `spin` keyframes.
- `prefers-reduced-motion: reduce` is honored globally in `app.css` (transitions/animations reduced to ~0); new animations need no extra handling as long as they use CSS transitions/animations.
- Keyboard focus MUST always be clearly visible (global `:focus-visible` outline) and not communicated by color change alone.

## 13. Accessibility

- Target: WCAG 2.2 AA. Text ≥ 4.5:1, large text and essential graphical controls ≥ 3:1 — per theme and per accent.
- All main functions are keyboard-reachable; focus order, accessible names, error messages and live status are semantically correct.
- Color is never the only carrier of selection, success, error, warning or permission (`StatusBadge` combines dot + label; selection adds a left bar and weight).
- Touch targets SHOULD be ≥ 44 × 44 px on touch layouts; dense desktop controls may be smaller with sufficient spacing and keyboard access.
- Layouts MUST tolerate longer German and English strings without cutting off essential functions. All user-facing text comes from the `.resx` files (both languages).

## 14. Framework policy

- The Web UI is plain Blazor + hand-written CSS: `app.css` (global tokens and primitives) plus scoped `*.razor.css` per component. There is no CSS bundler.
- **Bootstrap is not loaded** (`App.razor` references only `app.css` and the scoped bundle). The files under `wwwroot/lib/bootstrap` are unused leftovers. Bootstrap class names (`card`, `form-control`, `table`, `alert`, `nav-tabs`, `badge`, `row`/`col-*`, …) MUST NOT be relied on for styling; they render unstyled.
- Native controls (`<button>`, `<select>`, `<input>`, checkbox, file input) MUST always carry a Kaimo class or sit inside a styled Kaimo container — a raw browser control is never finished UI.
- Scoped CSS reaches child components' elements via `::deep`. Styles used by more than one page move to `app.css` or a shared component, not copy-paste.

## 15. Anti-patterns

Kaimo Files MUST NOT:

- tint every interactive or selected surface with the accent;
- render all icons in the accent color;
- hide important functions only behind "…" menus, right-click or hover;
- use large empty areas as a synonym for simplicity;
- consist only of floating cards;
- treat light mode as "all white" and dark mode as "all dark grey";
- create hierarchy by color alone;
- hard-code colors, radii or fonts instead of tokens;
- assume the accent is green;
- imitate consumer-cloud aesthetics, marketing dashboards, or cyberpunk/neon looks;
- copy older Windows UIs pixel by pixel;
- ignore native platform conventions for the sake of artificial sameness.

## 16. Decision order for concrete designs

When page specs or generated designs are ambiguous:

1. Make function and state understandable.
2. Keep important actions visible and reachable.
3. Build hierarchy through layout and neutral layers.
4. Meet accessibility and platform conventions.
5. Reuse an existing component or primitive before creating a new one.
6. Use the accent deliberately for orientation and brand.
7. Add decoration only if it does not weaken the points above.

## 17. Acceptance criteria

A design complies only if every answer is "yes":

- Can the UI be decomposed into navigation, content, commands, details and status without instructions?
- Are frequent main functions visible without discovering a hidden menu?
- Does the UI stay calm and scannable despite visible functions?
- Are dark and light mode each structured by several distinguishable neutral layers?
- Is the accent used sparingly and meaningfully — and does the screen still work with every selectable accent?
- Does the interface remain structurally understandable in greyscale?
- Do lists, toolbars, tree views, panels, dialogs and forms look like parts of one system (shared components and classes)?
- Are important states recognizable without color?
- Does every value come from a token (no hard-coded colors, radii, fonts)?
- Is no element left in a browser or framework default look?
- Are all strings localized in both `.resx` files?
- Does it feel like a modern, robust workbench and not a retro skin?

## 18. Known deviations (audit 2026-09-27)

The implementation largely matches this spec. Undefined tokens, hard-coded fallback colors, literal radii and dark-only border colors found in the audit were fixed on 2026-09-27; the provider colors are now tokens (`--provider-*` in `:root`). The following spots still diverge:

| # | Location | Deviation | Fix |
|---|---|---|---|
| 1 | `app.css` `--font-body` | Headings, brand and micro-labels render in Plus Jakarta Sans, but `--font-body` (body text) points to `'Inter'`, whose font file does not exist in `wwwroot/fonts/`, so body text falls back to the system font (Segoe UI on Windows). Buttons, inputs and selects additionally do not inherit the page font (no `font: inherit` reset), so they always use the browser's system font. | Set `--font-body` to `'Plus Jakarta Sans'`, drop the Inter `@font-face`, and add `button, input, select, textarea { font: inherit; }` to the reset. |
| 2 | `UserList.razor` (`.admin-tabs entity-tabs`) | Only remaining use of the old segmented tab control (Users/Groups/Roles switcher), restyled locally. | Optional: migrate to `<Tabs>` if it should look like the other tab strips. |
| 3 | `LogViewer` `.log-exclude-chip`, `ClientDeviceSettings` `.pill`, `MainLayout` `.demo-banner`, `UserList` (2×) | Fully rounded pill shapes (`border-radius: 999px`), which §7.1 reserves for small round indicators. | Optional: `--radius-control`, if they should match the crisp look. |
| 4 | `wwwroot/lib/bootstrap` | 8.4 MB of unused files. | May be deleted. |

## 19. Reusable master prompt

> Design the requested Kaimo Files screen according to the Kaimo design spec. Treat Kaimo Files as a robust, distinctive file-server workbench. Base the interaction logic on the strengths of classic Windows work surfaces: visible commands, clear zones, toolbars, lists, tree views, list + detail panels and explicit status indicators — without a retro look. Use Plus Jakarta Sans as the typeface (uppercase micro-labels for section and column labels), crisp 2–4 px radii, the 4 px spacing scale and accessible interactions. Build the UI mostly from clearly distinguishable neutral layers, using only the tokens from `app.css`. Use the accent (`var(--accent)` and its variants — user-selectable, green by default) sparingly for primary action, focus, selection and brand; never hard-code it or tint the whole UI. Dark and light mode must each have several high-contrast layers. Reuse `Tabs`, `StatusBadge`, `PrincipalPickerField`, `MultiSelect`, `InfoButton` and the global button, form, list, dialog and banner classes before inventing anything new. Important functions must not live only in context, hover or overflow menus. Define all relevant interaction and system states, and leave no element in a browser default look.

---

This document defines the shared design language. Page-specific information architecture, content, component assignment and workflows are specified in downstream specs.
