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
| `--surface` | a raised panel: an input, a tinted callout's base, the host's error bar | `input`, the callouts, `#blazor-error-ui` |
| `--surface-2` | a second step up, for a code span or a table header's ground | reserved for the components that need a second step |
| `--line` | the hairline: borders and table rules | `--hairline`, the filter pills, the table rows |
| `--ink` | body text | `body`, `input`, the brand, the error bar |
| `--muted` | secondary text: labels, hints, the signed-in user, the pager's count | `.user`, `.hint`, `.filters-label`, `.summary dt`, `.pager span`, `.grid th` |
| `--accent` | links, the primary button's fill, the focus ring, the current filter's fill | `.nav-link`, `.back`, `.filter`, `.link-button`, `button`, `:focus-visible` |
| `--on-accent` | text on an accent fill | `button`, `.filter[aria-current]` |
| `--danger` | a failure: error text, the destructive button's fill, an invalid input's border | `.error`, `.validation-message`, `button.danger`, `.error-panel`, `#blazor-error-ui` |
| `--on-danger` | text on a danger fill | `button.danger` |
| `--success` | something that went well: the notification's border and tint | `.notice` |
| `--on-success` | text on a success fill | a success fill, when a component takes one |
| `--warning` | something to look at before it fails | a warning callout, when a component takes one |
| `--on-warning` | text on a warning fill | the same |

### Type

| Token | Role | Used by |
|---|---|---|
| `--font-sans` | the face of everything but code: the system stack | `body` |
| `--font-mono` | code spans and request ids | `code` |
| `--text-xs` | 12 px: uppercase labels | `.grid th`, `.filters-label` |
| `--text-sm` | 14 px: hints, meta, form labels, the signed-in user, the pager's count | `.hint`, `.user`, `.form label`, `.pager span`, `.validation-message`, `.filter` |
| `--text-md` | 16 px: body | `body` |
| `--text-lg` | 18 px: the brand and section headings | `.brand`, `h2` |
| `--text-xl` | 24 px: the page heading | `h1` |
| `--weight-regular` | 400: body | `.link-button` |
| `--weight-medium` | 500: labels, buttons, links in the top bar | `.nav-link`, `.form label`, `button`, `.filter`, `.grid th` |
| `--weight-semibold` | 600: headings and the brand | `h1`, `h2`, `.brand` |
| `--leading` | the body line height | `body` |
| `--tracking-heading` | headings sit a little tighter | `h1`, `h2`, `.brand` |
| `--tracking-label` | uppercase labels sit a little wider | `.grid th`, `.filters-label` |

### Space, shape and devices

| Token | Role | Used by |
|---|---|---|
| `--space-1` | 4 px | the gap inside a label, the pill's vertical padding |
| `--space-2` | 8 px | cell padding, the gap between actions and filters |
| `--space-3` | 12 px | the gap between form fields, the back link's margin |
| `--space-4` | 16 px | the page's side gutter, the top bar's gap and padding |
| `--space-6` | 24 px | the content's vertical padding, the error panel's side padding |
| `--space-10` | 40 px | the error bar's room for its dismiss button |
| `--radius-sm` | 6 px: inputs, buttons, the notice | `input`, `button`, `.notice` |
| `--radius-md` | 10 px: panels | `.error-panel` |
| `--radius-lg` | 16 px: a card wider than a form | a card, when a component takes one |
| `--radius-pill` | a full round | `.filter` |
| `--hairline` | the one-pixel line in `--line` | `.topbar`, `input`, `.grid th`, `.grid td`, `.filter` |
| `--focus-ring` | the focus outline: two pixels of accent | `:focus-visible` |
| `--focus-offset` | the gap between the outline and the element | `:focus-visible` |
| `--page-width` | the measure of the page shell | `.page` |
| `--form-width` | the measure of a form | `.form` |

## Scales and rules

- **Weights** 400, 500 and 600; nothing bolder. **Sizes** 12, 14, 16, 18 and 24 px, as the type tokens
  name them. Headings track `--tracking-heading`; uppercase labels are 12 px with `--tracking-label`.
  Numbers in a table are tabular figures (`font-variant-numeric: tabular-nums`), so columns line up.
- **Edges** are one-pixel hairlines in `--line`; no shadows. **Radii** 6, 10, 16 or a pill.
- **One breakpoint**, 50 rem, is the system's; nothing shipped changes shape at it yet. The top bar and
  the filter row wrap wherever they run out of room, the page keeps `--page-width` at every width, and
  rows that read as blocks below it are the next step on this system.
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
signed-in name in `--muted`; `.content` is the `<main>`.

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

### Buttons

`button` is the primary action: an accent fill with `--on-accent` text, `--radius-sm`, the body font.
`.danger` on a button is the destructive variant (`--danger` fill, `--on-danger` text); `.link-button`
is a button that reads as a link (no fill, accent text) for an action that is not a navigation, such as
sign-out or dismiss. A disabled button fades to 60 % and loses its pointer; it is never hidden.

```html
<button type="submit" disabled="@_busy">@(_busy ? "Creating…" : "Create draft")</button>
<button class="danger" @onclick="VoidAsync">Void</button>
<button type="button" class="link-button" aria-label="Dismiss notification">✕</button>
```

An icon-only button carries an `aria-label`. A navigation is an `<a>` or a `NavLink`, never a button.

### Forms

`.form` stacks its fields at `--form-width`; each `label` wraps its input and its message, so the label
needs no `for`. `input` sits on `--surface` inside a hairline; `input[aria-invalid="true"]` turns the
hairline `--danger`. `.validation-message` (under its input) and `.validation-errors` (a list) are the
framework's classes for what validation says, in `--danger` and `--text-sm`; `.hint` is secondary help
in `--muted`; `.error` is the form's one `role="alert"` paragraph.

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

### Table

`.grid` is a full-width table with hairline rows; its headers are uppercase `--text-xs` labels in
`--muted`; `.num` right-aligns a numeric column with tabular figures. A column whose cells are links
gets a header read by screen readers only: `.visually-hidden`.

```html
<table class="grid">
  <thead><tr><th>Customer</th><th class="num">Total</th><th><span class="visually-hidden">Actions</span></th></tr></thead>
  <tbody>
    <tr><td>Ada Lovelace</td><td class="num">120.00 EUR</td><td><NavLink href="invoices/…" aria-label="View the invoice for Ada Lovelace">View</NavLink></td></tr>
  </tbody>
</table>
```

A repeated link names its target in an `aria-label`. Below the breakpoint the table keeps its columns
today; rows that read as blocks, each cell led by its header, are the next step on this system.

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

`.pager` is a `<nav aria-label="Pagination">`: a previous and a next button around a `span` that names
the page and the count in `--muted`, `aria-live="polite"` so the count is announced when it changes.

```html
<nav class="pager" aria-label="Pagination">
  <button disabled>Previous</button>
  <span aria-live="polite">Page 1 of 3 (42 invoices)</span>
  <button>Next</button>
</nav>
```

### Notifications and alerts

`.notifications` is the layout's live region (`role="status"`, always in the page, a stack with a
`--space-2` gap); a `.notice` inside it is a success message on a `--success` hairline and tint with a
`.link-button` to dismiss it. `.error-panel` is the error boundary's `role="alert"` block on a
`--danger` hairline and tint, with a heading, a sentence and a retry button. `.error` is a one-line
failure in `--danger`, next to what failed.

```html
<div class="notifications" role="status" aria-live="polite">
  <p class="notice"><span>Invoice issued.</span><button type="button" class="link-button" aria-label="Dismiss notification">✕</button></p>
</div>
<div class="error-panel" role="alert"><h1>Something went wrong</h1><p>…</p><button>Retry</button></div>
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

To see both themes, switch the operating system's appearance: the tokens follow it through
`color-scheme`. To hold one theme while you look, set `data-theme="light"` or `"dark"` on `<html>` in the
browser's developer tools. The page tests in `tests/*.ApiTests/Ui/` render the pages' markup without a
browser; a browser flow lives in the optional `UiTests` project.

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
attribute on `<html>` holds one theme regardless of the system. Nothing shipped sets that attribute
yet; a switch in the top bar that writes it, remembers it in the browser and offers System, Light and
Dark is the next step on this system, and when it lands this section names it.

## Mocks and the export

A mock of a new screen is an HTML page that links the two stylesheets and uses the components above:
hand one to an agent with the component names and it can draft the page as an artifact before any
Razor is written, in both themes, at phone and laptop width. The tokens are also a data set: a script
that writes them as `tokens.json` (name, light and dark value, role) for a design tool or a published
design-system page is the planned export; until it exists, the [Tokens](#tokens) tables and
`tokens.css` are the source.
