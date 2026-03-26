cmake_minimum_required(VERSION 3.22.1)
project("audiooverlan")

# Find Oboe via Prefab
find_package(oboe REQUIRED CONFIG)

include_directories(${CMAKE_CURRENT_SOURCE_DIR}/opus_lib/include)

add_library(opus SHARED IMPORTED)
set_target_properties(opus PROPERTIES IMPORTED_LOCATION ${CMAKE_CURRENT_SOURCE_DIR}/opus_lib/lib/${ANDROID_ABI}/libopus.so)

add_library(${CMAKE_PROJECT_NAME} SHARED
    aaudio_player.cpp
    aaudio_recorder.cpp
    rnnoise_wrapper.cpp
    native_opus.cpp
)

target_link_libraries(${CMAKE_PROJECT_NAME}
    android
    log
    oboe::oboe
    opus
)
