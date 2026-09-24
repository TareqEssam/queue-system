#!/usr/bin/env bash
# نشر نسخة Windows portable من Linux/macOS (يتطلب .NET 8 SDK)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/publish-win"

echo "==> Publishing self-contained win-x64..."
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

echo "==> Output: $OUT"
echo "انسخ هذا المجلد إلى جهاز Windows. لا يحتاج صلاحيات أدمن."
echo "اختياري: ضع cloudflared.exe بجانب QueueSystem.exe لتفعيل النفق العام."
