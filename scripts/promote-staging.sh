#!/usr/bin/env bash
set -euo pipefail

# The only script allowed to write into data/. Called after normalize-all.sh,
# validate-data (0 errors), and build-metadata have all already succeeded
# against the staging directory - this script does not re-validate anything.
#
# Each file is staged under a temp name in data/ itself and then renamed into
# place, so a crash mid-promotion never leaves a truncated canonical file
# (same technique BuildMetadataGenerator.WriteIfValid uses for build.json).
# True all-5-files-at-once atomicity isn't achievable on a plain filesystem;
# this is the closest practical approximation, and it only ever runs after
# every prerequisite gate above has already passed.

staging_dir="${1:?staging directory required}"
data_dir="${2:?data directory required}"

mkdir -p "$data_dir"

for file in pals.json passive-skills.json partner-skills.json game-settings.json build.json; do
  src="$staging_dir/$file"
  test -f "$src" || { echo "promote-staging: missing '$src' - refusing to promote." >&2; exit 1; }

  dest="$data_dir/$file"
  tmp="$dest.tmp-$$"
  cp "$src" "$tmp"
  mv -f "$tmp" "$dest"
done
