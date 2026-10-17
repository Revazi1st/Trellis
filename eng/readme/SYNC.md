# Keeping the two READMEs in step

[`README.redesign.md`](../../README.redesign.md) is a candidate front page for this repository, in
the walkthrough style of the [amantinband/clean-architecture](https://github.com/amantinband/clean-architecture)
README, with pictures from this folder. Until the maintainer chooses between it and
[`README.md`](../../README.md), both stay current: a change to any part below lands in both files in
the same pull request. Neither ships in a generated project: `.template.config/template.json` and
`eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj` exclude `README.redesign.md` and `eng/readme/`,
and Template CI fails if either appears in the output.

## Word for word in both

- [ ] **Install commands**: the two-line nuget.org block and the three-line clone block, comments
  included.
- [ ] **Toggles**: the "Toggles (checkboxes in Visual Studio / Rider, flags on the CLI):" line, the
  table and its ¹ footnote. A new toggle's row also goes into
  [`eng/Trellis.TemplatePack/README.md`](../Trellis.TemplatePack/README.md), whose multi-tenancy row
  carries the footnote inside the cell (step 8 of the
  [`add-toggle`](../../.claude/skills/add-toggle/SKILL.md) skill).
- [ ] **Credits**: from "Trellis takes ideas from other people's work." to "Packages carry their own
  licenses; …". A new credit line goes into both ([CONTRIBUTING.md](../../CONTRIBUTING.md)). Outside
  that block, the redesign keeps `<a name="credits"></a>` above its heading (whose own anchor is
  `#credits-`), so the `#credits` links in CONTRIBUTING.md and the nuget.org README keep working if
  it replaces README.md, and one sentence after the block credits its own layout.
- [ ] **License**: the paragraph that starts "MIT — see LICENSE".
- [ ] **Shared badges**: CI, Template CI, .NET 10 and License, same image and same link; renaming a
  workflow file changes both. On purpose, only the redesign shows CodeQL and GitHub stars, and it
  keeps its NuGet badges in an HTML comment until v0.1.0 is on nuget.org, while README.md already
  shows one.

From the repository root, in bash or zsh, this prints only its last line while the two agree:

```bash
shared() {  # the blocks both READMEs carry word for word, in file order
  sed -n -e '/^dotnet new install Trellis\.Templates$/,/^dotnet new trellis -n Your\.Project$/p' \
         -e '/^git clone https:\/\/github\.com\/Revazi1st\/Trellis\.git$/,/^dotnet new trellis -n Your\.Project -o Your\.Project$/p' \
         -e '/^Toggles (checkboxes/,/prints the reminder)\.$/p' \
         -e '/^Trellis takes ideas from other people/,/^Packages carry their own licenses/p' \
         -e '/^MIT — see \[LICENSE\]/,/the build enforces\.$/p' "$1"
}
badges() { grep -E '^\[!\[(CI|Template CI|\.NET 10|License: MIT)\]' "$1" | sort; }
diff <(shared README.md) <(shared README.redesign.md) &&
  diff <(badges README.md) <(badges README.redesign.md) &&
  echo "README.md and README.redesign.md are in step"
```

Each block is found by its first and last line, so rewording one of those lines means updating its
pattern here too.

## Same facts, different words

When one of these changes, reread the other file: the prerequisites; the run commands and ports (the
AppHost launch profiles, and 5171, 5170 and 5433 for the dev container); the seeded admin; the
dependency rule and the ring table; the invoice walkthrough and the code excerpts; the list of skills.

## The pictures

`render-diagrams.py` in this folder draws the redesign's
`{title,rings,architecture,testing,pipeline}-{light,dark}.svg` from the solution tree. Run it again
when a project, a feature folder or a test project is added, renamed or removed, and commit the SVGs
with that change. README.md has no pictures, so only the redesign can go stale this way.

## If the redesign replaces README.md

1. Move `README.redesign.md` over `README.md`, and delete this file.
2. Take `README.redesign.md` out of the two exclude lists and out of Template CI's must-not-ship list;
   keep `eng/readme/` in all three.
3. In Credits, move "the visual walkthrough-style README" out of the *when adopted* line (the
   feature-first persistence folders stay there), and drop the sentence below the block if it then
   repeats it.
4. Point step 8 of the `add-toggle` skill and the Credits rule in CONTRIBUTING.md back at one README.
5. Once v0.1.0 is on nuget.org, move the NuGet badges out of their comment.
