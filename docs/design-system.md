# Design system

The visual language of the Blazor UI: a small set of named values in two themes, the components the
pages are built from, the rules that keep text readable, and the tests that hold all of it. The starting
palette and the rules come from the template's visual language; the construction is the project's to
re-skin in one file (ADR-0032, [adr/0032-ui-design-system.md](adr/0032-ui-design-system.md)).

## What this is

Two stylesheets in `UI.Common/wwwroot/`, linked by the host's `App.razor` in this order:

- **`tokens.css`** holds every value: the colours of both themes, the type scale, the spacing, the radii
  and the devices (the hairline, the focus ring). It is the one file a project edits to make the UI its
  own, and the only file that may write a colour.
- **`app.css`** holds the components: one rule set per class the pages use, every colour a `var(--…)`
  of a token or a `color-mix()` of two, never a literal.

The pages and layouts carry classes only: no `style=` attribute, no `<style>` block, no `.razor.css`
file. A new visual element is a new component here, with its subsection under [Components](#components),
or `DesignSystemTests` fails the build.

## Tokens

The names are roles, not colours: `--accent` is whatever the project's accent is, so a re-skin changes
values and never a rule. Each colour is written once as a light and a dark value; see
[Themes and the switch](#themes-and-the-switch).

### Colour

| Token | Role | Used by |
|---|---|---|
| `--bg` | the page | `body` |
| `--surface` | a raised panel: an input, a secondary button, a tinted callout's or pill's base, the host's error bar | `input`, `select`, `.secondary`, `.callout`, the status pills' tints, `#blazor-error-ui` |
| `--surface-2` | a second step up: the neutral status pill's fill, and a code span's or a table header's ground when a component takes one | `.pill` |
| `--line` | the hairline: borders and table rules | `--hairline`, the filter pills, the status pill, the secondary button, the table rows |
| `--ink` | body text | `body`, `input`, `select`, the brand, `.secondary`, `.pill`, the error bar |
| `--muted` | secondary text: labels, hints, the signed-in user, the pager's count | `.user`, `.hint`, `.label`, `.summary dt`, `.pager span` |
| `--accent` | links, the primary button's fill, the focus ring, the current filter's fill, a callout's default tone | `a`, `.nav-link`, `.back`, `.filter`, `.link-button`, `button`, `.callout`, `:focus-visible` |
| `--on-accent` | text on an accent fill | `button`, `.filter[aria-current]` |
| `--danger` | a failure: error text, the destructive button's fill, an invalid input's border, the error boundary's callout, a voided status | `.error`, `.validation-message`, `button.danger`, `.callout.danger`, `.pill.danger`, `#blazor-error-ui` |
| `--on-danger` | text on a danger fill | `button.danger` |
| `--success` | something that went well: the notification's and a paid status's border and tint | `.callout.success`, `.pill.success` |
| `--on-success` | text on a success fill | a success fill, when a component takes one |
| `--warning` | something to look at before it fails | `.pill.warning`, and a warning callout when a page needs one |
| `--on-warning` | text on a warning fill | the same |

### Type

| Token | Role | Used by |
|---|---|---|
| `--font-sans` | the face of everything but code: the system stack | `body` |
| `--font-mono` | code spans and request ids | `code` |
| `--text-xs` | 12 px: uppercase labels | `.label` |
| `--text-sm` | 14 px: hints, meta, form labels, the signed-in user, the pager's count, a status | `.hint`, `.user`, `.form label`, `.pager span`, `.validation-message`, `.filter`, `.pill`, `.theme-select` |
| `--text-md` | 16 px: body | `body` |
| `--text-lg` | 18 px: the brand and section headings | `.brand`, `h2` |
| `--text-xl` | 24 px: the page heading | `h1` |
| `--weight-regular` | 400: body | `.link-button` |
| `--weight-medium` | 500: labels, buttons, links in the top bar, a status | `.nav-link`, `.form label`, `button`, `.filter`, `.label`, `.pill` |
| `--weight-semibold` | 600: headings and the brand | `h1`, `h2`, `.brand` |
| `--leading` | the body line height | `body` |
| `--tracking-heading` | headings sit a little tighter | `h1`, `h2`, `.brand` |
| `--tracking-label` | uppercase labels sit a little wider | `.label` |

### Space, shape and devices

| Token | Role | Used by |
|---|---|---|
| `--space-1` | 4 px | the gap inside a label, the filter pill's and the theme switch's vertical padding, a table cell's below the breakpoint |
| `--space-2` | 8 px | cell padding, the gap between actions and filters, the status pill's and the theme switch's side padding |
| `--space-3` | 12 px | the gap between form fields, the back link's margin |
| `--space-4` | 16 px | the page's side gutter, the top bar's gap and padding |
| `--space-6` | 24 px | the content's vertical padding, a callout's side padding |
| `--space-10` | 40 px | the error bar's room for its dismiss button |
| `--radius-sm` | 6 px: inputs and buttons | `input`, `select`, `button` |
| `--radius-md` | 10 px: panels | `.callout` |
| `--radius-lg` | 16 px: a card wider than a form | a card, when a component takes one |
| `--radius-pill` | a full round | `.filter`, `.pill` |
| `--hairline` | the one-pixel line in `--line` | `.topbar`, `input`, `select`, `.grid th`, `.grid td`, `.filter`, `.pill`, `.secondary` |
| `--focus-ring` | the focus outline: two pixels of accent | `:focus-visible` |
| `--focus-offset` | the gap between the outline and the element | `:focus-visible` |
| `--page-width` | the measure of the page shell | `.page` |
| `--form-width` | the measure of a form | `.form` |

## Scales and rules

- **Weights** 400, 500 and 600; nothing bolder. **Sizes** 12, 14, 16, 18 and 24 px, as the type tokens
  name them. Headings track `--tracking-heading`; uppercase labels are 12 px with `--tracking-label`.
  Numbers in a table are tabular figures (`font-variant-numeric: tabular-nums`), so columns line up.
- **Edges** are one-pixel hairlines in `--line`; no shadows. **Radii** 6, 10, 16 or a pill.
- **One breakpoint**, 50 rem, is the system's: below it a table's rows read as blocks, each cell a line
  led by its header's label ([Table](#table)). The top bar and the filter row wrap wherever they run out
  of room, and the page keeps `--page-width` at every width.
- **Focus** is the `--focus-ring` with `--focus-offset`, visible on every focusable element, never
  suppressed; the accent passes 3:1 on both grounds so the ring reads.
- **Text sits on a ground it passes AA on**: body text and the status colours on `--bg` or `--surface`
  at 4.5:1, `--ink` on `--surface-2` too, and text on a coloured fill is that fill's `--on-*` token,
  also at 4.5:1. A tint is `color-mix()` of a colour at 12 % or less over a ground, so the ground's
  ratios hold.
- **Every colour is a light/dark pair** written once; the light theme keeps the dark one's construction
  (the same elements, hairlines, radii, spacing and type) and only the values change.
- **Tokens are opaque** six-digit hex: a translucent colour composites differently on every ground and
  cannot be checked once.

## Components

The pages are built from these and nothing else. Each subsection names its classes, the states the
stylesheet styles, what the markup owes accessibility, and how it behaves on a narrow screen.

### Page shell

`.page` centres the content at `--page-width` with a `--space-4` gutter; `.topbar` is a flex row under a
hairline holding `.brand` (the product name, in `--ink`) and `.nav` (`aria-label="Main"`), whose
`.nav-link`s are accent text, the current one underlined by the framework's `.active`; `.user` shows the
signed-in name in `--muted`; the [theme switch](#theme-switch) ends the nav; `.content` is the `<main>`.

```html
<div class="page">
  <header class="topbar">
    <a class="brand" href="">Product</a>
    <nav class="nav" aria-label="Main"><NavLink href="invoices" class="nav-link">Invoices</NavLink></nav>
  </header>
  <main class="content">…</main>
</div>
```

The top bar wraps rather than overflowing at any width: nothing in it has a fixed width. A page sets its
`<PageTitle>` and has one `h1`, its other headings in order below it; the error panel's `h1` takes the
page's place only when the page has failed.

### Theme switch

`.theme` is the `label` that wraps the switch at the end of the top bar's nav, an inline flex row;
`.theme-select` is its `select`, the compact control: `--text-sm` with `--space-1` by `--space-2` padding,
on the input's `--surface`, hairline and `--radius-sm`. Its options are System, Light and Dark; the one
the browser keeps is selected once the page is interactive, System until then
([Themes and the switch](#themes-and-the-switch)).

```html
<label class="theme">
  <span class="visually-hidden">Theme</span>
  <select class="theme-select" aria-label="Colour theme">
    <option value="system" selected>System</option>
    <option value="light">Light</option>
    <option value="dark">Dark</option>
  </select>
</label>
```

A select, never a button that cycles: its three options are announced by name, and nobody has to know
what comes after the current one. It carries a label, visible or visually hidden (`.visually-hidden`
here, with an `aria-label` that names it to a screen reader), is operated from the keyboard like any
select and shows the focus ring. It sits outside `AuthorizeView`, so it is there signed out too, and it
wraps with the top bar on a narrow screen.

### Buttons

`button` is the primary action: an accent fill with `--on-accent` text, `--radius-sm`, the body font.
`.danger` on a button is the destructive variant (`--danger` fill, `--on-danger` text); `.secondary`
is a quieter action on `--surface` inside a hairline with `--ink` text, such as the pager's Previous
and Next; `.link-button` is a button that reads as a link (no fill, accent text) for an action that is
not a navigation, such as sign-out or dismiss. A disabled button fades to 60 % and loses its pointer;
it is never hidden.

```html
<button type="submit" disabled="@_busy">@(_busy ? "Creating…" : "Create draft")</button>
<button class="danger" @onclick="VoidAsync">Void</button>
<button class="secondary" @onclick="NextAsync">Next</button>
<button type="button" class="link-button" aria-label="Dismiss notification">✕</button>
```

An icon-only button carries an `aria-label`. A navigation is an `<a>` or a `NavLink`, never a button.

### Forms

`.form` stacks its fields at `--form-width`; each `label` wraps its input and its message, so the label
needs no `for`. `input` and `select` sit on `--surface` inside a hairline; `input[aria-invalid="true"]`
turns the hairline `--danger`. `.validation-message` (under its input) and `.validation-errors` (a list)
are the framework's classes for what validation says, in `--danger` and `--text-sm`; `.hint` is
secondary help in `--muted`; `.error` is the form's one `role="alert"` paragraph.

```html
<EditForm Model="_model" OnValidSubmit="SubmitAsync" class="form">
  <DataAnnotationsValidator />
  <ServerValidation @ref="_server" />
  <label>Customer name<InputText @bind-Value="_model.CustomerName" /><ValidationMessage For="() => _model.CustomerName" /></label>
  @if (_error is not null) { <p class="error" role="alert">@_error</p> }
  <button type="submit">Create draft</button>
</EditForm>
```

One alert per failure; each field's message stays under its field. The form is already narrower than
the breakpoint, so it does not change below it.

### Label

`.label` is the label strip: `--text-xs` at `--weight-medium`, uppercase with `--tracking-label`, in
`--muted`. The filter row's `.filters-label` and a table's headers (`.grid th`) are this one rule, and
so is the label a table cell shows below the breakpoint; a new small label takes `.label` rather than a
rule of its own.

```html
<span class="label">Show</span>
```

The markup stays sentence case; the stylesheet uppercases it.

### Table

`.grid` is a full-width table with hairline rows; its headers are the label strip; `.num` right-aligns
a numeric column with tabular figures. A column whose cells are links gets a header read by screen
readers only: `.visually-hidden`. Every other cell carries `data-th` with its header's text.

```html
<table class="grid">
  <thead><tr><th>Customer</th><th>Status</th><th class="num">Total</th><th><span class="visually-hidden">Actions</span></th></tr></thead>
  <tbody>
    <tr><td data-th="Customer">Ada Lovelace</td><td data-th="Status"><span class="pill">Issued</span></td><td data-th="Total" class="num">120.00 EUR</td><td><NavLink href="invoices/…" aria-label="View the invoice for Ada Lovelace">View</NavLink></td></tr>
  </tbody>
</table>
```

A repeated link names its target in an `aria-label`; like every link without a class of its own, it is
accent text by the stylesheet's base `a` rule. Below the breakpoint the rows read as blocks: the
header row is left to screen readers, each row sits under a hairline, and each cell is a line led by
its `data-th` in the label strip with its value at the end; the label keeps its width and a long value
wraps beside it, so a phone never scrolls sideways and no label breaks. A cell under a
`.visually-hidden` header, the row's link, carries no `data-th` and shows no label. A header and its
cells' `data-th` change together.

### Status pill

`.pill` is a record's status as a static chip, never a link or a button: `--text-sm` at
`--weight-medium` in `--ink` on `--surface-2`, inside a hairline, `--radius-pill` round. A variant
sets its tone, `var(--tone)`, to a status colour, which draws the hairline and tints the fill at
12 % over `--surface` under the same `--ink` text: `.pill.success`, `.pill.warning` and
`.pill.danger`.

```html
<td data-th="Status"><span class="pill success">Paid</span></td>
<dd><span class="pill">Draft</span></dd>
```

The word is always in the pill: colour is never the only signal. An invoice's status maps in one
helper the list and the detail page share: Paid is `.pill.success`, Voided `.pill.danger`, Draft and
Issued the neutral `.pill`. A pill never wraps, in a table cell or a summary, at any width.

### Summary list

`.summary` is a definition list in two columns, the terms in `--muted`: the record's key facts above its
detail.

```html
<dl class="summary">
  <dt>Status</dt><dd>Draft</dd>
  <dt>Total</dt><dd>120.00 EUR</dd>
</dl>
```

### Actions

`.actions` is the row of buttons that act on the record, `--space-2` apart, under the detail.

```html
<div class="actions">
  <button @onclick="PayAsync">Mark paid</button>
  <button class="danger" @onclick="VoidAsync">Void</button>
</div>
```

### Back link

`.back` is the way back to the list above a child page's heading, there whatever the load state; an
arrow precedes its text and it underlines on hover.

```html
<NavLink href="invoices" class="back" Match="NavLinkMatch.All">Back to invoices</NavLink>
<h1>New invoice</h1>
```

### Filter row

`.filters` is a `role="group"` labelled by its `.filters-label` (an uppercase label in `--muted`); each
`.filter` is a pill link with a hairline and accent text, and `.filter[aria-current]` is the current one,
filled with the accent. The filter lives in the address, so the pills are links, not buttons.

```html
<div class="filters" role="group" aria-labelledby="invoice-filter-label">
  <span id="invoice-filter-label" class="filters-label">Show</span>
  <a href="invoices" class="filter" aria-current="page">All</a>
  <a href="invoices?status=Draft" class="filter">Draft</a>
</div>
```

The row wraps at any width: a filter that does not fit moves to the next line.

### Pager

`.pager` is a `<nav aria-label="Pagination">`: a previous and a next button, both `.secondary`, around
a `span` that names the page and the count in `--muted`, `aria-live="polite"` so the count is announced
when it changes.

```html
<nav class="pager" aria-label="Pagination">
  <button class="secondary" disabled>Previous</button>
  <span aria-live="polite">Page 1 of 3 (42 invoices)</span>
  <button class="secondary">Next</button>
</nav>
```

### Notifications and alerts

`.callout` is a message on a panel: a one-pixel line in its tone, `var(--tone)`, `--radius-md`, and
the tone tinted at 8 % over `--surface` under `--ink` text. The tone is the accent; `.callout.success`
sets it to `--success` and `.callout.danger` to `--danger`. `.notifications` is the layout's live region
(`role="status"`, always in the page, a stack with a `--space-2` gap); a `.callout.success` inside it
is a success notification, laid out as a row (`.notifications .callout`) with a `.link-button` to
dismiss it. The error boundary's panel is a `.callout.danger` with `role="alert"`: a heading, a
sentence and a retry button. `.error` is a one-line failure in `--danger`, next to what failed.

```html
<div class="notifications" role="status" aria-live="polite">
  <p class="callout success"><span>Invoice issued.</span><button type="button" class="link-button" aria-label="Dismiss notification">✕</button></p>
</div>
<div class="callout danger" role="alert"><h1>Something went wrong</h1><p>…</p><button>Retry</button></div>
```

Success is a notification, failure stays next to what failed; one alert per failure.

### Host error bar

`#blazor-error-ui` is the host's circuit-failure bar: hidden until the framework shows it, fixed to the
bottom on `--surface` under a two-pixel `--danger` edge, with a `.reload` link and a `.dismiss` button
(an icon with an `aria-label`).

```html
<div id="blazor-error-ui" role="alert" data-nosnippet>
  An unhandled error has occurred.
  <a href="." class="reload">Reload</a>
  <button type="button" class="dismiss" aria-label="Dismiss">✕</button>
</div>
```

### Focus

`:focus-visible` on any element is the `--focus-ring` offset by `--focus-offset`. No component sets
`outline: none`.

## Voice

- Sentence case everywhere: headings, buttons, labels, table headers (the stylesheet uppercases the
  small labels; the markup stays sentence case).
- A button is a verb: "Create draft", "Issue invoice", "Mark paid"; while it works it says so,
  "Creating…".
- An alert is a full sentence with a full stop, and says what to do when there is something to do:
  "Someone else changed this in the meantime." A notification is the past tense of the action:
  "Invoice issued."
- An empty state names the filter: "No overdue invoices." A count names its noun: "42 invoices".
- Secondary text explains, it does not warn: "Add at least one line before issuing."

## Proof

A change is looked at 400 px and 1280 px wide, in both colour schemes, before it is finished: no horizontal
overflow at the phone width, the page at `--page-width` on the laptop.

`DesignSystemTests` (`tests/*.ArchitectureTests/`, compiled with the UI) holds the system to its
rules, each fact with a positive control that fails a planted violation:

- `Only_the_token_file_writes_a_colour`: no hex, `rgb()`, `hsl()`, `oklch()` or named colour, no
  `style=` attribute and no `<style>` block in any `.css` or `.razor` under the UI except `tokens.css`.
- `Every_colour_token_is_written_once_for_both_schemes`: every colour in `tokens.css` is one
  `light-dark(#rrggbb, #rrggbb)`, and the `data-theme` blocks set nothing but `color-scheme`.
- `Text_passes_AA_on_its_grounds_and_every_on_token_on_its_fill`: the contrast rules above, computed
  from the token values in both schemes; a failure names the token, the ground, the scheme and the ratio.
- `Every_class_a_page_uses_has_a_rule_and_every_rule_is_documented`: each static `class="…"` in a
  `.razor` file has a rule in `app.css`, each class `app.css` styles is named in [Components](#components),
  and each class named there exists.
- `Every_variable_a_stylesheet_reads_is_declared`: every `var(--…)` in `app.css` names a token or an
  alias the stylesheet declares.
- `The_design_doc_and_the_token_file_name_the_same_tokens`: the [Tokens](#tokens) tables and
  `tokens.css` name the same set, both ways.

The `ui-reviewer` subagent (`.claude/agents/ui-reviewer.md`) reads a change to a page, a layout or a
stylesheet against this document, `.claude/rules/ui.md` and the accessibility each component owes, in a
browser at phone and laptop width when one is running, and reports; run it before a UI change is
finished. Its findings are claims to check against the code or one of the tests above.

To see both themes, use the [theme switch](#theme-switch) in the top bar: Light and Dark hold one, and
System follows the operating system's appearance through `color-scheme`. Setting `data-theme="light"`
or `"dark"` on `<html>` in the browser's developer tools does the same for one page without keeping it.
The page tests in `tests/*.ApiTests/Ui/` render the pages' markup without a browser; a browser flow
lives in the optional `UiTests` project.

## Re-skinning

1. Change the values in `tokens.css`, each colour as its light and dark pair. Keep the names: the
   components and the tests read them.
2. Run `DesignSystemTests`. The contrast fact tells you which pair fell short and by how much; move the
   value, not the rule. A light accent that is text on a light ground is the usual failure.
3. Change the faces in `--font-sans` and `--font-mono` if the product has its own; ship the font files
   from `wwwroot/` and keep a system fallback in the stack.
4. The brand is the `.brand` text in the top bar; a logo goes in its place as an `<img>` with `alt`.

Nothing else is a re-skin: a new element is a component with a subsection here; a new colour is a token
pair with its `--on-*`, and the contrast fact decides whether it may be text.

## Themes and the switch

`:root` declares `color-scheme: light dark`, so every `light-dark()` value follows the operating
system. `:root[data-theme="light"]` and `:root[data-theme="dark"]` set only `color-scheme`, so an
attribute on `<html>` holds one theme regardless of the system: `data-theme` wins over
`prefers-color-scheme`, and without it the system decides.

The [theme switch](#theme-switch) at the end of the top bar's nav writes that attribute. It offers
System, Light and Dark: Light or Dark is kept in the browser's `localStorage` under the key `theme` and
written as `data-theme`; System removes both, and the page follows the system again. Two files make it:

- **`wwwroot/theme.js`** in UI.Common. UI.Web's `App.razor` loads it in `<head>`, before the two
  stylesheets, as a plain blocking `<script src>`: it runs before the first paint, so a kept Dark never
  flashes light. It reads the key inside `try` (a private window may refuse storage, and the page then
  follows the system), accepts only `light` or `dark`, puts the choice back after an enhanced navigation
  (which rewrites `<html>`'s attributes), and exposes one global, `window.appTheme`, with `get()`
  (`"light"`, `"dark"` or `"system"`) and `set(value)`. It is a file, not an inline script, because the
  web host's Content-Security-Policy is `script-src 'self'` (`UiConventionTests` refuses an inline
  `<script>` in any `.razor` file); it is the one script besides Blazor's.
- **`Layout/ThemeToggle.razor`**, the select, placed in `MainLayout`'s nav after the `AuthorizeView`.
  It reads the kept choice through `IJSRuntime` after its first render and writes a new one when the
  select changes, never during a render (a prerender has no browser to call). A host that does not load
  `theme.js` still renders it, on System, and a choice made there is shown but applies nothing: the read
  and the write both catch the failed call, so the page goes on. `IJSRuntime` belongs to the Components
  stack and works in every render mode, so UI.Common stays render-mode-agnostic (ADR-0015).

The `/Error` page's layout has no switch (it renders statically, with no circuit to run one), yet it
takes the kept theme: `theme.js` runs from the head every page shares. To drop the switch, delete the
two files, the script tag, `<ThemeToggle />` and the `.theme` rules; the tokens follow the system again.

## Mocks and the export

A mock of a new screen is an HTML page that links the two stylesheets and uses the components above:
hand one to an agent with the component names and it can draft the page as an artifact before any
Razor is written, in both themes, at phone and laptop width.

The tokens are also a data set. `scripts/export-design-tokens.sh` writes them as JSON for a design tool
or a published design-system page to import: an array with one object per custom property `tokens.css`
declares under `:root`, in the file's order, each a `name`, a `value` and a `usage`. A colour's value is
its pair, `{"light": "#rrggbb", "dark": "#rrggbb"}` in lower case; any other value is the string as
written, its whitespace collapsed; the usage is the token's *Role* cell in the [Tokens](#tokens) tables,
empty for a token without a row. A value may not hold a `;`, `{` or `}` inside quotes or parentheses
(a data URL, say): the script stops with exit 2 and names the token rather than write it cut short. The
same files give the same bytes, on Linux and macOS alike.

```bash
./scripts/export-design-tokens.sh                  # the JSON on stdout
./scripts/export-design-tokens.sh -o tokens.json   # or into a file
./scripts/export-design-tokens.sh --tokens <file> --doc <file>   # another token file and doc
```

The export is generated, never edited: `tokens.css` and the tables stay the source, and
`DesignSystemTests` (`The_token_export_names_every_token_with_both_values`) runs the script and holds
every name and value to `tokens.css` and every usage to the tables, so it cannot drift from either. It
is POSIX `sh` and `awk` with nothing to install; the fact skips on Windows, where there is no `/bin/sh`.
