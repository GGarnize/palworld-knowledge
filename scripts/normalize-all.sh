#!/usr/bin/env bash
set -euo pipefail

# Runs the four normalize-* commands into a staging directory, deliberately
# separate from data/. Nothing under data/ is touched by this script - see
# promote-staging.sh for the only step allowed to write there.
#
# No --mappings is passed anywhere here on purpose: the Linux Dedicated
# Server was verified (locally, by force-downloading depot 2394012 and
# running every normalize-* command against it) to expose every property
# DT_PalMonsterParameter / DT_PassiveSkill_Main / DT_PartnerSkillParameter /
# BP_PalGameSetting / the pt-BR and en L10N name tables need, with byte-for-
# byte identical output to the previously-published data/*.json. If a future
# game update changes that, the affected `dotnet run -- normalize-*` call
# below fails loudly and this script (and the workflow step calling it)
# fails with it - there is no silent mappings fallback to keep this honest.

extractor_project="${1:?path to the PalworldAtlas.Extractor project required}"
pak_dir="${2:?pak directory required}"
staging_dir="${3:?staging output directory required}"
build_id="${4:?current Steam build ID required (used to check runtime-evidence compatibility)}"
runtime_evidence="${5:-}"

mkdir -p "$staging_dir"

run_extractor() {
  dotnet run --project "$extractor_project" --configuration Release --no-build -- "$@"
}

run_extractor normalize-pals \
  --pak-dir "$pak_dir" \
  --output "$staging_dir/pals.json"

run_extractor normalize-passives \
  --pak-dir "$pak_dir" \
  --output "$staging_dir/passive-skills.json"

run_extractor normalize-partner-skills \
  --pak-dir "$pak_dir" \
  --output "$staging_dir/partner-skills.json"

# Runtime evidence is hand-captured via UE4SS and cannot be regenerated in
# CI. It's only trusted for the exact build it records itself as belonging
# to (evidenceFor.steamBuildId) - never assumed current just because a file
# happens to exist. If it doesn't match (including the "never proven, so
# always null" case), --runtime-evidence is simply not passed through, and
# normalize-game-settings falls back to its own existing, already-documented
# behavior: omit 'transport' entirely rather than publish it under a build
# it was never verified against.
use_runtime_evidence=false
if [ -n "$runtime_evidence" ] && [ -f "$runtime_evidence" ]; then
  evidence_build_id="$(jq -r '.evidenceFor.steamBuildId // empty' "$runtime_evidence")"
  if [ -n "$evidence_build_id" ] && [ "$evidence_build_id" = "$build_id" ]; then
    use_runtime_evidence=true
  else
    echo "WARNING: '$runtime_evidence' is not verified against build $build_id (evidenceFor.steamBuildId=${evidence_build_id:-null}) - game-settings.json will omit 'transport'. Re-verify the runtime CDO values against this build via UE4SS and update evidenceFor.steamBuildId to restore it." >&2
  fi
fi

if [ "$use_runtime_evidence" = "true" ]; then
  run_extractor normalize-game-settings \
    --pak-dir "$pak_dir" \
    --output "$staging_dir/game-settings.json" \
    --runtime-evidence "$runtime_evidence"
else
  run_extractor normalize-game-settings \
    --pak-dir "$pak_dir" \
    --output "$staging_dir/game-settings.json"
fi
