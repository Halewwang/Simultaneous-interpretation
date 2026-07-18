#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
for test in icon-pipeline-test.sh app-bundle-test.sh \
  lifecycle-scripts-test.sh package-pipeline-test.sh; do
  bash "$ROOT/Packaging/Tests/$test"
done
echo "PASS: all packaging tests"
