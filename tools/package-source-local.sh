#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESTINATION="${1:-}"

ruby - "$ROOT" <<'RUBY'
require "digest"
require "json"
require "pathname"
require "time"

root = Pathname(ARGV.fetch(0)).realpath
excluded_directories = %w[bin obj artifacts .git .vs .vscode .idea TestResults coverage packages]
excluded_names = %w[.DS_Store Thumbs.db SOURCE_MANIFEST.sha256.json]
excluded_extensions = %w[.user .suo .tmp .bak .orig .log .dmp .stackdump .db .sqlite .sqlite3 .sqlite-wal .sqlite-shm .pfx .p12 .key .pem .mobileprovision]

files = Dir.glob(root.join("**", "*"), File::FNM_DOTMATCH).each_with_object([]) do |candidate, selected|
  path = Pathname(candidate)
  next unless path.file?
  relative = path.relative_path_from(root).to_s
  parts = relative.split(File::SEPARATOR)
  next unless (parts & excluded_directories).empty?
  next if excluded_names.include?(path.basename.to_s)
  next if excluded_extensions.include?(path.extname)
  next if path.basename.to_s == ".env" || (path.basename.to_s.start_with?(".env.") && path.basename.to_s != ".env.example")
  next if parts.length == 1 && path.basename.to_s.match?(/^RadioVault\.(Client|Server)-.+-Setup\.exe$/)
  selected << [relative, path]
end

entries = files.sort_by(&:first).map do |relative, path|
  { "path" => relative, "bytes" => path.size, "sha256" => Digest::SHA256.file(path).hexdigest }
end
commit = ENV["RADIOVAULT_BUILD_IDENTITY"] || ENV["GITHUB_SHA"]
if commit.to_s.empty?
  commit = `git -C #{root.to_s.dump} rev-parse HEAD 2>/dev/null`.strip
end
commit = nil unless commit.to_s.match?(/\A[0-9a-f]{7,64}\z/i)
source_dirty = commit ? !`git -C #{root.to_s.dump} status --porcelain 2>/dev/null`.strip.empty? : false
short = commit ? commit.downcase[0, 12] + (source_dirty ? ".dirty" : "") : "local"
manifest = {
  "version" => root.join("VERSION.txt").read.strip,
  "buildIdentity" => "#{root.join("VERSION.txt").read.strip}+#{short}",
  "commit" => commit&.downcase,
  "sourceDirty" => source_dirty,
  "generatedAtUtc" => Time.now.utc.iso8601(7),
  "fileCount" => entries.length,
  "files" => entries
}
root.join("SOURCE_MANIFEST.sha256.json").write(JSON.pretty_generate(manifest) + "\n")
puts "Source manifest: #{entries.length} files"
RUBY

if [[ -n "$DESTINATION" ]]; then
  case "$DESTINATION" in
    /*) ARCHIVE="$DESTINATION" ;;
    *) ARCHIVE="$ROOT/$DESTINATION" ;;
  esac
  STAGING="$(mktemp -d "${TMPDIR:-/tmp}/radiovault-source.XXXXXX")"
  trap 'rm -rf "$STAGING"' EXIT
  mkdir -p "$(dirname "$ARCHIVE")" "$STAGING/RadioVault-Source"
  rsync -a \
    --exclude '/.git/' --exclude '/.vs/' --exclude '/.vscode/' --exclude '/.idea/' \
    --exclude 'bin/' --exclude 'obj/' --exclude 'artifacts/' --exclude 'TestResults/' \
    --exclude 'coverage/' --exclude 'packages/' --exclude '.DS_Store' --exclude 'Thumbs.db' \
    --exclude '*.user' --exclude '*.suo' --exclude '*.tmp' --exclude '*.bak' --exclude '*.orig' \
    --exclude '*.log' --exclude '*.dmp' --exclude '*.stackdump' --exclude '*.db' --exclude '*.sqlite*' \
    --exclude '*.pfx' --exclude '*.p12' --exclude '*.key' --exclude '*.pem' --exclude '*.mobileprovision' \
    "$ROOT/" "$STAGING/RadioVault-Source/"
  rm -f "$ARCHIVE"
  ditto -c -k --sequesterRsrc --keepParent "$STAGING/RadioVault-Source" "$ARCHIVE"
  echo "Source archive: $ARCHIVE"
fi
