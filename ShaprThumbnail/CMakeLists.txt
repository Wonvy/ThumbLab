cmake_minimum_required(VERSION 3.20)
project(ShaprThumbnail LANGUAGES C CXX)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_CXX_COMPILER_ARCHITECTURE_ID STREQUAL "x64")
    message(FATAL_ERROR "Build with the Visual Studio x64 C++ tools for Windows.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options($<$<COMPILE_LANGUAGE:CXX>:/W4> $<$<COMPILE_LANGUAGE:CXX>:/permissive-> /utf-8)
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

add_library(ShaprThumbnail SHARED
    ThumbnailProvider.cpp ProjectArchive.cpp ModelPreview.cpp PreviewCache.cpp
    third_party/puff/puff.c ShaprThumbnail.def)
target_link_libraries(ShaprThumbnail PRIVATE bcrypt gdi32 ole32 shell32 uuid windowscodecs)

add_custom_target(ShaprThumbnailRegistration ALL
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/install.cmd"
        "${CMAKE_CURRENT_SOURCE_DIR}/uninstall.cmd"
        "${CMAKE_CURRENT_SOURCE_DIR}/Registration.ps1"
        "${CMAKE_CURRENT_SOURCE_DIR}/SetModelPreview.ps1"
        "${CMAKE_CURRENT_SOURCE_DIR}/set-preview.cmd"
        "$<TARGET_FILE_DIR:ShaprThumbnail>"
    DEPENDS ShaprThumbnail
    VERBATIM)

include(CTest)
if(BUILD_TESTING)
    add_executable(ShaprThumbnailSmoke ThumbnailSmoke.cpp)
    target_link_libraries(ShaprThumbnailSmoke PRIVATE gdi32 ole32 shell32 shlwapi uuid)
    add_dependencies(ShaprThumbnailSmoke ShaprThumbnail)
    add_test(NAME ShaprThumbnailCom
        COMMAND ShaprThumbnailSmoke "$<TARGET_FILE:ShaprThumbnail>")

    find_package(Python3 QUIET COMPONENTS Interpreter)
    if(Python3_Interpreter_FOUND)
        set(preview_fixture "${CMAKE_CURRENT_BINARY_DIR}/preview-fixture.shapr")
        add_test(NAME ShaprThumbnailFixture
            COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/MakePreviewFixture.py" "${preview_fixture}")
        set_tests_properties(ShaprThumbnailFixture PROPERTIES FIXTURES_SETUP shapr_preview)
        add_test(NAME ShaprThumbnailModelPreview
            COMMAND ShaprThumbnailSmoke "$<TARGET_FILE:ShaprThumbnail>" "${preview_fixture}")
        set_tests_properties(ShaprThumbnailModelPreview PROPERTIES FIXTURES_REQUIRED shapr_preview)
    endif()
endif()
