# trashsweepr

[Leia em português](README.md)

A free Windows app that helps with two things: **finding out what's filling up your disk** and **trimming the
excess out of Windows 11 without breaking anything**.

It's an open source project under the [MIT license](LICENSE), and it was built with the help of artificial
intelligence (see [how](#built-with-the-help-of-ai)).

![Disk section](screenshot-disk-en.png)

## What it does

**Disk.** Shows your disk as a map of blocks, sized by each folder and file. Unlike other analyzers, it understands
what it's looking at: it recognizes build and cache folders (`node_modules`, `target`, `bin/obj`, `.venv`...), checks
that the project is really there, and tells you which command brings everything back. It also remembers previous
scans, so you can see what grew since last week.

**Windows Cleanup.** A list of tweaks for removing apps that ship with Windows (including Copilot, OneDrive and
Edge) and turning off telemetry and ads. Each tweak explains what it does and how risky it is, and everything can be
simulated first and undone later.

## Getting started

1. Download the latest `.zip` from [Releases](https://github.com/carlosholak/trashsweepr/releases) and extract it to a folder.
2. Open `trashsweepr.exe`. If the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) is
   missing, Windows itself offers to download it.
3. Click **Scan drive**. That's it.

Keep the `tweaks` folder next to the `.exe`: that's where the list of cleanup tweaks lives.

The app shows up in Portuguese when Windows is in Portuguese, and in English for any other language.

<details>
<summary>Opening it from the command line</summary>

```
trashsweepr.exe [folder] [--tab recoverable|changes|largest] [--cleanup]
```

Passing a folder starts scanning it right away (dropping a folder onto the `.exe` does the same), `--tab` opens one
of the side panel tabs, and `--cleanup` opens straight into Windows Cleanup.

</details>

## Disk

Pick a drive or a folder and wait for the map to appear. Double-click opens a folder, **Backspace** goes back,
**F5** rescans just the open folder and **Esc** cancels. The colors can show the file type, the age, what's
rebuildable or what changed.

The side panel has four tabs:

- **Folder**: what's in the open folder, with size and percentage.
- **Largest**: the 200 largest files anywhere below it.
- **Rebuildable**: build and cache folders, the command that recreates them and how long the project has been idle.
  From here you can send what you don't need to the Recycle Bin.
- **Changes**: what appeared, grew, shrank or disappeared since an earlier scan.

Nothing is really deleted. Whatever you pick goes to the Windows Recycle Bin after a confirmation, and you can
restore it from there. And the app only offers to remove folders it recognized as rebuildable: there's no "delete
anything" button.

<details>
<summary>What counts as rebuildable</summary>

A folder is only flagged if the project file is right next to it. A stray `bin` or `build` in the middle of your
documents doesn't count.

| Folder | Needs next to it | How it comes back |
|---|---|---|
| `node_modules`, `.next`, `.nuxt`, `.svelte-kit`, `.turbo`, `.parcel-cache` | `package.json` | `npm install` / `npm run build` |
| `target` | `Cargo.toml` | `cargo build` |
| `bin`, `obj` | `*.csproj`, `*.fsproj` or `*.vbproj` | `dotnet build` |
| `.venv`, `venv` | `pyproject.toml`, `requirements.txt`, `setup.py` or `Pipfile` | `python -m venv` + `pip install` |
| `.gradle`, `build` | `build.gradle(.kts)` or `settings.gradle(.kts)` | `gradlew build` |
| `.dart_tool`, `build` | `pubspec.yaml` | `flutter pub get` / `flutter build` |
| `build` | `CMakeLists.txt` | `cmake --build` |
| `__pycache__`, `.pytest_cache`, `.mypy_cache`, `.ruff_cache` | nothing | they come back on their own |

The global caches of npm, Yarn, pnpm, pip, NuGet, Cargo and Gradle show up too.

</details>

<details>
<summary>How it remembers scans</summary>

Every complete scan saves a small summary (folders and files from 1 MB up; about 45 KB for 34 thousand files)
in `%LOCALAPPDATA%\trashsweepr\snapshots`, keeping up to 12 per folder. Smaller files that haven't been modified
since then count as unchanged.

</details>

## Windows Cleanup

![Windows Cleanup section (sample data)](screenshot-cleanup-en.png)

Each tweak shows what it does, its risk (**Safe**, **Moderate** or **Advanced**) and how to undo it. When you open
the section, the app checks what is already applied on your machine and leaves out, with the reason, anything that
doesn't fit your version of Windows.

So you don't have to check everything by hand, there are profiles: **Light** checks only the safe ones,
**Moderate** adds the moderate ones, and **Custom** is you picking. Advanced and optional tweaks (such as removing
Teams, OneDrive or Edge) only go if you check them.

### Taking care of your machine

Messing with Windows is scary, and rightly so. That's why:

- **Simulation is on by default.** It shows exactly what would be done, down to the text of the commands, without
  touching anything.
- **Before applying**, the app creates a restore point and saves the previous value of everything it's about to change.
- **Every change is verified** right after it's made. If part of it fails, the rest of that tweak is undone automatically.
- **Undo** works per tweak or all at once.
- **Some things are never touched**: Microsoft Store, Windows Update, Defender, networking, audio, system components,
  WebView2 and Edge Update. That list lives in the code, so no tweak file can change it.
- **No scripts in the tweaks.** Tweaks only describe what to change; the commands are fixed in the app.

The app opens without asking for administrator rights, because the disk map doesn't need them. In Windows Cleanup you
can browse and simulate freely; when it's time to apply, it offers **Reopen as administrator** and comes back straight
to the same screen.

<details>
<summary>About removing apps, OneDrive and Edge</summary>

- **Apps** (Copilot, Clipchamp, Xbox...) are removed for your user only. The package files usually stay on disk and
  "Undo" registers the app again; if Windows has already deleted them, just reinstall from the Microsoft Store.
- **OneDrive** is removed with the official uninstaller that ships with Windows. Files already on your PC stay where
  they are, but "online-only" files stop opening until you reinstall it.
- **Edge** is removed with Edge's own uninstaller, in an Advanced tweak. Microsoft only allows full removal in some
  regions (the European Economic Area); elsewhere Windows refuses and the app tells you nothing was changed. "Undo"
  reinstalls Edge with winget, which needs internet.

Everything is logged to `%ProgramData%\trashsweepr\limpeza\logs`, and the backups go to
`%ProgramData%\trashsweepr\limpeza\backups` (the folder keeps its original Portuguese name so backups from earlier
versions are still found).

</details>

## Built with the help of AI

trashsweepr was developed together with Claude, by Anthropic, using Claude Code. In practice: I decided what the app
should do, how it should behave and what was acceptable to change in Windows; the AI wrote much of the code, the tests
and the texts, and I reviewed and approved them. No command that changes the system went into Windows Cleanup without
being explained and approved first.

To offset the risk of AI-written code, the project has over 500 automated tests that run against a fake Windows (no
real tweak is ever executed in the tests), and GitHub Actions runs all of them on every change. Even so, use the
simulation before applying, especially for Moderate and Advanced tweaks.

## Open source

All the code is here, under the [MIT license](LICENSE): you can use, study, modify and redistribute it. Found a
problem or have an idea? Open an [issue](https://github.com/carlosholak/trashsweepr/issues). Pull requests are welcome
too.

As with any MIT software, it comes with no warranty: it's a personal project, made with care, but applying the tweaks
is the user's responsibility.

## To build:

(You need the .NET 8 SDK)

```bash
dotnet build trashsweepr.sln
dotnet test trashsweepr.sln
dotnet publish src/trashsweepr/trashsweepr.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

To publish a new version: bump `<Version>` in `src/trashsweepr/trashsweepr.csproj`, write its section in
[CHANGELOG.md](CHANGELOG.md) and push the tag (`git tag vX.Y.Z && git push origin vX.Y.Z`). GitHub Actions runs the
tests, builds the `.zip` and creates the release.

<details>
<summary>Environment variables for testing</summary>

- `TRASHSWEEPR_UI_SNAPSHOT_DIR=<folder>` saves PNGs of the rendered interface.
- `TRASHSWEEPR_READONLY_SMOKE=1` runs detection and simulation on your actual Windows, read-only.
- `TRASHSWEEPR_TEST_CULTURE=en-US` runs the tests in another language (the default is `pt-BR`, whatever the Windows language).
- `TRASHSWEEPR_INTEGRATION=1` tests registry access in a temporary key (`HKCU\Software\CleanupTests`) that is deleted at the end.
- `TRASHSWEEPR_LANG=en-US` (or `pt-BR`) opens the app itself in another language, for testing and screenshots.

</details>

<details>
<summary>Where things are</summary>

- `src/trashsweepr`: the WPF app. The Disk section sits at the root (`Scanner`, `Recoverables`, `Snapshots`,
  `RecycleBin`, `Coloring`, `Squarify`, `TreemapControl`, `MainWindow`) and Windows Cleanup in `Cleanup/`
  (ViewModels and Views). Interface texts are in `Resources/Strings.resx` (English) and `Resources/Strings.pt.resx` (Portuguese).
- `src/Cleanup/Core`: the cleanup engine, with no WPF: tweaks and validation, protection list, planning, backup,
  restore and logging. Its messages are in `Resources/Messages.resx` and `Messages.pt.resx`.
- `src/Cleanup/Infrastructure`: what actually talks to Windows (registry, PowerShell, services, tasks, apps,
  programs, restore points).
- `tweaks`: the tweaks in JSON, the profiles and the schema.
- `tests`: tests for the engine, the interface and the integration with the fake Windows.

</details>
