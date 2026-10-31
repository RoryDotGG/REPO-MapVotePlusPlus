#!/usr/bin/env bash
# Publishes a built package zip to Thunderstore with the same API calls tcli makes: start an upload,
# PUT each part to its presigned URL, finish the upload, then submit it to the community.
# Usage: THUNDERSTORE_TOKEN=<service account token> publish-thunderstore.sh dist/MapVotePlusPlus-<version>.zip
set -euo pipefail

team=RoryDotGG
community=repo
categories='["mods", "quality-of-life", "ai-generated"]'
api=https://thunderstore.io/api/experimental

zip=${1:?Usage: publish-thunderstore.sh <package zip>}
: "${THUNDERSTORE_TOKEN:?Set THUNDERSTORE_TOKEN to a Thunderstore service account token}"

name=$(unzip -p "$zip" manifest.json | jq -r .name)
version=$(unzip -p "$zip" manifest.json | jq -r .version_number)

# A version can only be uploaded once, so a re-run after a later step failed skips this.
if curl -sf -o /dev/null "$api/package/$team/$name/$version/"; then
  echo "$team-$name $version is already on Thunderstore, skipping."
  exit 0
fi

post() {
  curl -sS --fail-with-body -X POST "$api/$1" \
    -H "Authorization: Bearer $THUNDERSTORE_TOKEN" \
    -H "Content-Type: application/json" \
    --data "$2"
}

upload=$(post usermedia/initiate-upload/ "$(jq -n --arg f "$(basename "$zip")" --argjson s "$(stat -c %s "$zip")" \
  '{filename: $f, file_size_bytes: $s}')")
uuid=$(jq -r .user_media.uuid <<<"$upload")

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
parts=()
while read -r number offset length url; do
  part=$work/part$number
  dd if="$zip" of="$part" iflag=skip_bytes,count_bytes skip="$offset" count="$length" status=none
  curl -sS --fail-with-body -T "$part" -D "$part.headers" \
    -H "Content-MD5: $(openssl md5 -binary "$part" | base64)" "$url" >&2
  etag=$(tr -d '\r' <"$part.headers" | sed -n 's/^[Ee][Tt][Aa][Gg]: *//p')
  parts+=("$(jq -n --arg e "$etag" --argjson n "$number" '{ETag: $e, PartNumber: $n}')")
done < <(jq -r '.upload_urls[] | "\(.part_number) \(.offset) \(.length) \(.url)"' <<<"$upload")

post "usermedia/$uuid/finish-upload/" "$(printf '%s\n' "${parts[@]}" | jq -s '{parts: .}')" >/dev/null

result=$(post submission/submit/ "$(jq -n --arg team "$team" --arg community "$community" --arg uuid "$uuid" \
  --argjson categories "$categories" \
  '{author_name: $team, categories: [], communities: [$community], community_categories: {($community): $categories},
    has_nsfw_content: false, upload_uuid: $uuid}')")
echo "Published $team-$name $version: $(jq -r .package_version.download_url <<<"$result")"
