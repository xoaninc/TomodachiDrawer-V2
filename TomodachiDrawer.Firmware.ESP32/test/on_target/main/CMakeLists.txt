# Pull the shared test logic and the parser under test from their normal
# locations in the firmware tree (no copies — single source of truth).
idf_component_register(
    SRCS "target_main.c"
         "${CMAKE_CURRENT_LIST_DIR}/../../tdld_tests.c"
         "${CMAKE_CURRENT_LIST_DIR}/../../../main/tdld_parser.c"
    INCLUDE_DIRS "."
                 "${CMAKE_CURRENT_LIST_DIR}/../../"
                 "${CMAKE_CURRENT_LIST_DIR}/../../../main")
