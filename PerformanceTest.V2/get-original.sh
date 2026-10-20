#!/bin/sh
# Extracts the library as it was before the redesign, at commit b512201, into Original/ for PerformanceTest.V2.
set -e
cd "$(dirname "$0")"
mkdir -p Original
for file in ConcurrenceNetworkPrefixLookup.cs NetworkPrefixKey.cs NetworkPrefixLookup.cs NetworkPrefixLookupBase.cs; do
    git show "b512201:Bitvantage.InternetProtocol.NetworkPrefixLookup/$file" > "Original/$file"
done
ls Original
