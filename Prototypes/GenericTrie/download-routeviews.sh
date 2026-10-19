#!/bin/sh
# Downloads the CAIDA RouteViews prefix-to-AS snapshots used by the prototype's real-data tests and benchmarks.
# Usage: download-routeviews.sh <target-folder>   then set ROUTEVIEWS_DIR=<target-folder>
# The data is published by CAIDA at https://www.caida.org/catalog/datasets/routeviews-prefix2as/
set -e
target="${1:?target folder required}"
mkdir -p "$target"
cd "$target"
curl -sSfO https://publicdata.caida.org/datasets/routing/routeviews-prefix2as/2025/09/routeviews-rv2-20250930-2200.pfx2as.gz
curl -sSfO https://publicdata.caida.org/datasets/routing/routeviews6-prefix2as/2025/09/routeviews-rv6-20250930-0200.pfx2as.gz
gunzip -f routeviews-rv2-20250930-2200.pfx2as.gz routeviews-rv6-20250930-0200.pfx2as.gz
ls -l "$target"
