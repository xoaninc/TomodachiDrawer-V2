# Compiles and runs the tdld_parser unit tests on the host with clang (or gcc).
# Requires a C compiler on PATH (e.g. LLVM-MinGW). Exit code mirrors the tests.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$mainDir = Join-Path $here "..\main"
$out = Join-Path $here "tdld_tests.exe"

$cc = (Get-Command clang -ErrorAction SilentlyContinue) ?? (Get-Command gcc -ErrorAction SilentlyContinue)
if (-not $cc) { Write-Error "No C compiler (clang/gcc) found on PATH."; exit 2 }

& $cc.Source -std=c11 -Wall -Wextra -I $mainDir `
    (Join-Path $here "host_main.c") `
    (Join-Path $here "tdld_tests.c") `
    (Join-Path $mainDir "tdld_parser.c") `
    -o $out
if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit 1 }

& $out
exit $LASTEXITCODE
