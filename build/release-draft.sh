#!/usr/bin/env bash
# Creates ONE draft GitHub release for a tag and attaches the files:  build/release-draft.sh <tag> <file>...
# Needs the gh CLI and GH_TOKEN. Refuses to run if a release (draft or published) already exists for the tag.
#
# Why not an action: uploading several files in parallel to a *draft* made softprops/action-gh-release create a separate release
# per upload (a draft cannot be looked up by its tag), which left duplicate releases for every tag.
set -euo pipefail

tag="${1:?usage: build/release-draft.sh <tag> <file>...}"; shift
[ "$#" -gt 0 ] || { echo "no files to attach" >&2; exit 2; }
for f in "$@"; do [ -f "$f" ] || { echo "not a file: $f" >&2; exit 2; }; done
repo="${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner -q .nameWithOwner)}"

# Drafts do not show up when asking for a tag, so list every release and compare tag names.
existing="$(gh api "repos/$repo/releases" --paginate -q ".[] | select(.tag_name == \"$tag\") | .id" | tr '\n' ' ')"
if [ -n "${existing// /}" ]; then
  echo "A release for $tag already exists (id: ${existing}). Delete it on GitHub, or upload to it by hand, then re-run." >&2
  exit 1
fi

# One call creates the release and uploads every file to it.
gh release create "$tag" "$@" --draft --title "$tag" --generate-notes --repo "$repo"
