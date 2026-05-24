// Host test runner: compile with clang/gcc alongside tdld_parser.c and
// tdld_tests.c, then run the resulting executable. Exit code 0 == all passed.
//
//   clang -I../main host_main.c tdld_tests.c ../main/tdld_parser.c -o tests.exe
//   ./tests.exe
//
// See run_host_tests.ps1 for the convenience wrapper.

#include "tdld_tests.h"

int main(void) {
    return tdld_run_all_tests() == 0 ? 0 : 1;
}
