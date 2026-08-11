#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"

OUT="../Analyzers"
dotnet build -c Release

mkdir -p "$OUT"
cp bin/Release/netstandard2.0/AceLand.Injection.SourceGenerator.dll "$OUT/"

echo "✔ copied to $OUT/AceLand.Injection.SourceGenerator.dll"
echo "  → switch to Unity, wait for recompile, then run"
echo "    Tools ▸ AceLand ▸ Injection ▸ Diagnostics"