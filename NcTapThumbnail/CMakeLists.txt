cmake_minimum_required(VERSION 3.20)
project(NcTapThumbnail LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_CXX_COMPILER_ARCHITECTURE_ID STREQUAL "x64")
    message(FATAL_ERROR "Build with the Visual Studio x64 C++ tools for Windows.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /permissive- /utf-8)
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

add_library(NcTapThumbnail SHARED ThumbnailProvider.cpp GCode.cpp NcTapThumbnail.def)
target_link_libraries(NcTapThumbnail PRIVATE gdi32 ole32 uuid)

add_custom_target(NcTapThumbnailRegistration ALL
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/install.cmd"
        "${CMAKE_CURRENT_SOURCE_DIR}/uninstall.cmd"
        "${CMAKE_CURRENT_SOURCE_DIR}/Registration.ps1"
        "$<TARGET_FILE_DIR:NcTapThumbnail>"
    DEPENDS NcTapThumbnail
    VERBATIM)

include(CTest)
if(BUILD_TESTING)
    add_executable(NcTapGCodeTests GCodeTests.cpp GCode.cpp)
    add_test(NAME NcTapGCode COMMAND NcTapGCodeTests)

    add_executable(NcTapThumbnailSmoke ThumbnailSmoke.cpp)
    target_link_libraries(NcTapThumbnailSmoke PRIVATE gdi32 ole32 shell32 shlwapi uuid)
    add_dependencies(NcTapThumbnailSmoke NcTapThumbnail)
    add_test(NAME NcTapThumbnailCom COMMAND NcTapThumbnailSmoke "$<TARGET_FILE:NcTapThumbnail>")
endif()
