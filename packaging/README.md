# SliderSorter

A standalone Windows tool that reads the mods installed in **Mod Organizer 2** and lets you assign clothing mods to **BodySlide slider groups** in bulk.

BodySlide's built-in Group Manager shows only a flat list of outfits — it has no idea which mod each outfit comes from. SliderSorter adds that missing dimension: **tick mods, assign them to a group in one click**, or expand a mod and fine-tune outfit by outfit.

## Highlights

- **Auto-discovers MO2** instances (global and portable); manual override in Settings → Environment. The outfit list replicates BodySlide's own project-path resolution and simulates the USVFS overlay under MO2, so it matches exactly what BodySlide will show.
- **Bulk assignment**: tick separators (= everything below), whole mods, or individual outfits; member badges and per-mod `[in group x/total]` counters show progress at a glance.
- **Rule-based grouping**: keyword rules (mod / outfit / exclude) with saved presets, a live hit preview, and one-click apply — good for large load orders.
- **New-mods reminder**: after installing new clothing mods, the next scan offers them in a tree for one-click assignment.
- **Output conflict page**: when outfits from different mods write to the same `.nif`, decide who builds it — per group, per mod priority, or by outfit keyword — then write `BuildSelection.xml` so BodySlide's batch build stops asking (the original file is backed up as `.bak`).
- **3D preview** of the source mesh with your installed textures (reads `.nif`, `.bsa`/`.ba2` archives and `.dds` directly).
- **View group** window with name filtering and batch "move to another group"; **30-step undo** (`Ctrl+Z`) for group operations.
- **Clean output**: one XML per group, named after the group; renamed/deleted groups leave no stale files. Output destinations: auto (recommended), BodySlide directory, a dedicated MO2-managed mod, real game Data, or a custom path.
- **Dark / light themes**, UI in **English / 中文 / Deutsch / Русский / Français**, four font sizes — everything switches live, no restart.

## Requirements

- Windows x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — only for the framework-dependent build (the self-contained build needs nothing)
- Mod Organizer 2 with your clothing mods enabled
- BodySlide (detected automatically; can be set manually in Settings)
- Works with any BodySlide-based game: Skyrim SE/AE, Fallout 4, etc. — groups are game-agnostic

## Installation

**This is a standalone tool, not a mod.** Extract the `SliderSorter` folder anywhere you like (Desktop is fine) and run `SliderSorter.exe`. Do **not** install it into the game, and do **not** install it with a mod manager as game data — it only needs disk-level read/write access to your MO2 instance and BodySlide.

You can add it to MO2's executable dropdown for convenience, but launching it directly works just as well.

> Note: the exe is unsigned, so Windows SmartScreen or your antivirus may show a one-time warning ("More info" → "Run anyway"). The build is reproducible from source.

## Quick start

1. Start SliderSorter — it finds your MO2 instance and lists all **enabled** mods that contain BodySlide outfits, in MO2's left-pane order (separators shown as headers).
2. Pick your profile; BodySlide is detected automatically (verify "Effective project path" in Settings → Environment).
3. On the right panel, create a group and select it. On the left panel, tick mods or individual outfits, then click **Add to group** in the middle.
4. Click **Save group file** (`Ctrl+S`), then restart BodySlide (and MO2 too, if you launch BodySlide through it) — the new groups appear in BodySlide's group dropdown.

The full manual is built into the app (F1 or Settings → Help, in all five languages); full documentation is on the [GitHub page](https://github.com/lin-414/SliderSorter).

## FAQ

**Can I use it while MO2 is running?** Yes — it reads `ModOrganizer.ini` / `modlist.txt` from disk. Restart BodySlide after saving so it picks up the new groups.

**My groups don't show up in BodySlide.** 1) Restart BodySlide (and MO2 if launched via MO2); 2) in Settings → Help → Diagnostics, check that "Effective project path" and the output target point to the same directory.

**Where do the group files go?** Wherever BodySlide actually reads them from: the BodySlide directory's `SliderGroups\`, or — under MO2 — a dedicated `SliderSorter Output` mod (cleanest: survives BodySlide reinstalls, switchable per profile). "Auto" picks the right one; all five modes are in Settings → Output.

**Why is it licensed GPL-3.0?** It parses `.nif` meshes with [Nifly](https://github.com/ousnius/NiflySharp), BodySlide's author's GPL-3.0 C# library, so the whole program must be GPL-3.0 too. Source code: [github.com/lin-414/SliderSorter](https://github.com/lin-414/SliderSorter). Third-party library licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
