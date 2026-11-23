# Contributing

Thanks for looking. LightSync is a personal project, built for one PC and tested only there, so
it moves at its own pace. Bug reports, ideas and small pull requests are welcome. For anything
larger, open an issue first so we can agree on it before you spend time on it.

## Reporting a bug or an idea

Use the issue templates. Please include the LightSync version (Help > About), your Windows version and
the devices involved (brand and model are enough).

Before you post, check that screenshots and logs show only what's needed: they can contain the names
of your apps, web sites, devices or your Windows user name. Security problems go through
[SECURITY.md](SECURITY.md), not a public issue.

## Building and testing

See [Requirements](README.md#requirements) in the README. Before a pull request, run:

```powershell
cargo clippy --workspace --all-targets
cargo test --workspace
dotnet build ui\LightSync.App -c Release -p:Platform=x64
```

Then try the change in the app on your own PC, and say in the pull request what you tested it on.

## Privacy rules for changes

LightSync keeps everything on the PC. A change must keep it that way:

- No telemetry, analytics, crash uploads or accounts.
- No new network requests. The only one today is the check for new OpenRGB versions; OpenRGB itself
  listens on 127.0.0.1 only.
- Key presses, sound, app names and web site hosts are used in memory for the lighting only, never
  stored, logged or sent.
- Read only what a feature needs (for a web site trigger, the host of the page, nothing else of the
  address), and only while it's in use.
- Settings stay in the user's own folders, and the service's pipe stays limited to the user who runs
  it.

## Style

- Keep changes small and focused; follow the code around them.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org) with a short,
  plain summary, for example `fix: keep the color when a trigger ends`.
- User-facing text is short and plain English.

By contributing you agree that your work is released under the project's [MIT license](LICENSE), and
you follow the [code of conduct](CODE_OF_CONDUCT.md).
