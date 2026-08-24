# Palworld Knowledge

A canonical, versioned base of Palworld game-mechanics data, extracted
directly from the current game/server files, structured for use as context
for LLMs and other tooling.

## What this is

Palworld's mechanics (Pals, passive skills, partner skills, base-camp
settings, work suitability, ...) live inside binary `.pak` assets that
change with every game patch. This project extracts and normalizes that
data into stable, schema-validated JSON, and keeps it in sync with the
game's Steam build automatically.

## Principles

- Official game/server data first - no wiki or fan-site scraping.
- Official PT-BR/EN localization, never machine-translated.
- Normalized, semantic datasets - not raw table dumps.
- Cross-dataset integrity validation (`validate-data`) before anything is published.
- Every publish carries build provenance (`data/build.json`): Steam build ID, per-dataset SHA-256, row counts, and a diff against the previous build.
- Automatic updates, triggered by Steam build ID changes - not on a blind schedule.
- Last-known-good preservation: if any validation step fails, the currently published data is left untouched.

## Datasets

| File | Contents |
|---|---|
| `data/pals.json` | Every Pal/variant row (base, boss, tower boss, raid boss, technical), PT-BR/EN names, movement stats, work suitability, natural passives, partner-skill linkage |
| `data/passive-skills.json` | All passive skill rows, including internal/non-displayable ones, with effects, activation conditions, and availability flags |
| `data/partner-skills.json` | Per-Pal partner skills: active skill, ranks, passive effects granted per rank, base-camp relevance |
| `data/game-settings.json` | Base-camp work-hard modes, work-suitability craft speeds, and (when available) runtime transport-speed evidence |
| `data/build.json` | Build provenance: Steam build ID, extractor version, per-dataset row count + SHA-256, validation summary, and comparison against the previous build |

Each dataset has a matching JSON Schema in `schemas/`, enforced by the
`validate-data` command before any publish.

## Origin

The extractor (`extractor/`) builds on the data-table reading and PAK/package
loading approach from [palworld-atlas-data](https://github.com/Awy64/palworld-atlas-data)
(MIT licensed - see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)) and
extends it with a different pipeline: semantic normalization
(`normalize-pals`, `normalize-passives`, `normalize-partner-skills`,
`normalize-game-settings`), schema + cross-dataset validation
(`validate-data`), and build metadata with change tracking
(`build-metadata`). This project has its own architecture, datasets, and
goals - it is not a fork tracked against that project's history.

## Automatic updates

`.github/workflows/update-palworld-data.yml` runs on a schedule (and on
demand): it checks the Palworld Dedicated Server's public Steam build ID,
and if it changed, downloads the Linux Dedicated Server package, normalizes
all four datasets from it (no `.usmap` mappings required - verified against
the current build), validates them, builds new provenance metadata, and
only then replaces `data/*.json` and commits. Any failure at any step
leaves the previously published data untouched.

## Disclaimer

Palworld and Pocketpair are trademarks/property of their respective owners.
This repository is not affiliated with or endorsed by Pocketpair.

This repository does **not** redistribute game assets. It contains only
normalized, derived data (names, stats, mechanics as structured JSON) - no
`.pak`/`.ucas`/`.utoc`/`.usmap` files, textures, raw extracted assets, or
server binaries are ever committed here.
