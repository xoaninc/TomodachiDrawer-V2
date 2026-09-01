#!/usr/bin/env bash
# Compiles and runs the tdld_parser unit tests on the host with clang (or gcc).
# POSIX counterpart of run_host_tests.ps1 — same compiler flags, same exit code.
#
# These are the only automated checks that exist on the ESP32 parser, and they matter more than
# usual because that firmware's binary cannot currently be rebuilt: no local ESP-IDF, no CI job.
# The source and the shipped binary can only be trusted to agree if the source's behaviour is
# pinned, which is what these do.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
main_dir="$here/../main"
out="$(mktemp -d)/tdld_tests"

if command -v clang >/dev/null 2>&1; then
  cc_bin=clang
elif command -v gcc >/dev/null 2>&1; then
  cc_bin=gcc
else
  echo "No C compiler (clang/gcc) found on PATH." >&2
  exit 2
fi

"$cc_bin" -std=c11 -Wall -Wextra -I "$main_dir" \
  "$here/host_main.c" \
  "$here/tdld_tests.c" \
  "$main_dir/tdld_parser.c" \
  -o "$out"

"$out"
