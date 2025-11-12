#!/usr/bin/env bash
set -euo pipefail

#
# Publish Umbraco 13 for Simply.com (IIS) — self-contained win-x86
# - Produces a clean publish folder
# - Writes a production web.config (InProcess) with sane env vars
# - Creates writable Logs/Data folders
# - (Optional) copies local Examine indexes to avoid heavy first-boot rebuild
# - Removes any accidental app_offline.* from the output
#
# Usage:
#   CONN_STR='Server=...;Database=...;User Id=...;Password=...;Encrypt=True;TrustServerCertificate=True;MultipleActiveResultSets=True' \
#   INCLUDE_EXAMINE=1 \
#   DETAILED_ERRORS=1 \
#   ./serverPublish.sh
#

### --- CONFIG ---
PROJECT="Lararafelagid.csproj"
RID="win-x86"
CONFIG="Release"

# Output dir
OUT_DIR="$(pwd)/publish-out/${RID}"

# Optional environment inputs
CONN_STR="${CONN_STR:-}"               # your production SQL connection string (recommended to pass via env)
INCLUDE_EXAMINE="${INCLUDE_EXAMINE:-}" # set to "1" to copy local Examine indexes
DETAILED_ERRORS="${DETAILED_ERRORS:-}" # set to "1" to enable ASPNETCORE_DETAILEDERRORS in web.config

### --- PRECHECKS ---
if ! command -v dotnet >/dev/null 2>&1; then
  echo "Error: dotnet CLI not found. Install .NET SDK and try again." >&2
  exit 1
fi

if [ ! -f "$PROJECT" ]; then
  echo "Error: can't find $PROJECT in $(pwd)" >&2
  exit 1
fi

### --- CLEAN / RESTORE / PUBLISH ---
echo "==> Cleaning…"
dotnet clean "$PROJECT" -c "$CONFIG"

echo "==> Restoring…"
dotnet restore "$PROJECT"

echo "==> Resetting output folder: $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "==> Publishing ($CONFIG, $RID, self-contained)…"
dotnet publish "$PROJECT" \
  -c "$CONFIG" \
  -r "$RID" \
  --self-contained true \
  -p:PublishIISAssets=true \
  -p:PublishReadyToRun=false \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:InvariantGlobalization=false \
  -o "$OUT_DIR"

### --- WRITE web.config (InProcess; no processPath needed) ---
echo "==> Writing web.config"
DETAILED_VAL="false"
if [ "${DETAILED_ERRORS}" = "1" ]; then DETAILED_VAL="true"; fi

# Use the provided connection string or a placeholder (you can edit the file before upload)
CONN_ELEM=""
if [ -n "$CONN_STR" ]; then
  # Escape backslashes and ampersands for XML attribute safety
  ESC_CONN_STR="${CONN_STR//&/&amp;}"
  CONN_ELEM="          <environmentVariable name=\"ConnectionStrings__umbracoDbDSN\" value=\"${ESC_CONN_STR}\" />"
else
  CONN_ELEM="          <!-- TODO: Set ConnectionStrings__umbracoDbDSN here or in appsettings.Production.json -->"
fi

cat > "$OUT_DIR/web.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <!-- Surface app errors instead of generic IIS pages -->
      <httpErrors existingResponse="PassThrough" />
      <aspNetCore hostingModel="InProcess"
                  stdoutLogEnabled="true"
                  stdoutLogFile=".\logs\stdout">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
          <environmentVariable name="ASPNETCORE_DETAILEDERRORS" value="${DETAILED_VAL}" />
          <environmentVariable name="DOTNET_gcServer" value="0" />
          <!-- Keep Umbraco lean and deterministic in Prod -->
          <environmentVariable name="Umbraco__CMS__Runtime__Mode" value="Production" />
          <environmentVariable name="Umbraco__CMS__ModelsBuilder__ModelsMode" value="SourceCodeManual" />
${CONN_ELEM}
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
EOF

### --- ENSURE REQUIRED FOLDERS IN OUTPUT ---
echo "==> Creating required folders (logs, umbraco/Logs, umbraco/Data/TEMP)…"
mkdir -p "$OUT_DIR/logs"
mkdir -p "$OUT_DIR/umbraco/Logs"
mkdir -p "$OUT_DIR/umbraco/Data/TEMP"

### --- OPTIONAL: COPY LOCAL EXAMINE INDEXES ---
if [ "$INCLUDE_EXAMINE" = "1" ]; then
  SRC_IDX="$(pwd)/umbraco/Data/TEMP/ExamineIndexes"
  if [ -d "$SRC_IDX" ]; then
    echo "==> Copying local Examine indexes to publish output"
    mkdir -p "$OUT_DIR/umbraco/Data/TEMP"
    rm -rf "$OUT_DIR/umbraco/Data/TEMP/ExamineIndexes"
    cp -R "$SRC_IDX" "$OUT_DIR/umbraco/Data/TEMP/ExamineIndexes"
  else
    echo "==> Skipping Examine copy (no local indexes at $SRC_IDX)"
  fi
fi

### --- SAFETY: REMOVE ANY app_offline FILES FROM OUTPUT ---
rm -f "$OUT_DIR/app_offline.htm" "$OUT_DIR/app_offline.html" || true

### --- SUMMARY + HELPER ZIP ---
echo "==> Publish complete → $OUT_DIR"
echo "==> Output top-level:"
find "$OUT_DIR" -maxdepth 1 -mindepth 1 -print

ZIP_NAME="deploy-${RID}-$(date +%Y%m%d%H%M).zip"
echo "==> Creating helper zip: $ZIP_NAME"
(
  cd "$OUT_DIR"
  zip -qr "../${ZIP_NAME}" .
)
echo "==> Ready to upload: $(dirname "$OUT_DIR")/${ZIP_NAME}"

cat <<'NEXTSTEPS'

==============================================================
UPLOAD / DEPLOY CHECKLIST (Simply.com / IIS)
==============================================================
1) On the server, back up /wwwroot/media (your media).
2) Delete EVERYTHING in the site root (including /wwwroot) to avoid stale files.
3) Upload the ENTIRE CONTENTS of the publish folder (or the zip) to the site root.
   - Do NOT skip /wwwroot. After upload, restore your /wwwroot/media backup if needed.
4) Confirm these exist in the root:
   - web.config
   - logs/           (empty is fine)
   - umbraco/Logs/   (empty is fine)
   - umbraco/Data/TEMP/ (contains ExamineIndexes if you included them)
5) Browse /umbraco or / once. Then check /logs/stdout_*.log for any startup issues.
6) When finished debugging, set ASPNETCORE_DETAILEDERRORS=false in web.config.

If you see HTTP 502.5 or 500:
- Ensure no custom UseUrls/Kestrel bindings in Program.cs (let IIS handle it).
- Ensure build is self-contained win-x86 and files aren’t mixed with prior publishes.
- If "Out of memory." appears, keep the flags above, and consider pre-seeding Examine indexes.
==============================================================
NEXTSTEPS
