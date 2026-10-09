cmake_minimum_required(VERSION 3.20)
project(UEFNStaticMappingsGenerator LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if (NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "x64 でビルドしてください (UE のモジュールは 64bit のみ)")
endif()

# 依存ライブラリも含めて静的 CRT で揃える
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

include(FetchContent)

set(ZSTD_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(ZSTD_BUILD_STATIC ON CACHE BOOL "" FORCE)
set(ZSTD_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ZSTD_LEGACY_SUPPORT OFF CACHE BOOL "" FORCE)
FetchContent_Declare(zstd
    GIT_REPOSITORY https://github.com/facebook/zstd.git
    GIT_TAG v1.5.7
    GIT_SHALLOW TRUE
    SOURCE_SUBDIR build/cmake)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BROTLI_DISABLE_TESTS ON CACHE BOOL "" FORCE)
set(BROTLI_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
FetchContent_Declare(brotli
    GIT_REPOSITORY https://github.com/google/brotli.git
    GIT_TAG v1.1.0
    GIT_SHALLOW TRUE)

FetchContent_MakeAvailable(zstd brotli)

add_executable(UEFNStaticMappingsGenerator
    src/Main.cpp
    src/PE.cpp
    src/Hook.cpp
    src/Dumper.cpp
    src/Mappings.cpp
    src/Compression.cpp
)

target_include_directories(UEFNStaticMappingsGenerator PRIVATE ${zstd_SOURCE_DIR}/lib)
target_link_libraries(UEFNStaticMappingsGenerator PRIVATE libzstd_static brotlienc brotlidec)
target_compile_definitions(UEFNStaticMappingsGenerator PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(UEFNStaticMappingsGenerator PRIVATE /W4 /utf-8 /permissive-)
