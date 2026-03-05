cmake_minimum_required(VERSION 3.22.1)
project("audiooverlan")

# Find Oboe via Prefab
find_package(oboe REQUIRED CONFIG)

add_library(${CMAKE_PROJECT_NAME} SHARED
    aaudio_player.cpp
    aaudio_recorder.cpp
)

target_link_libraries(${CMAKE_PROJECT_NAME}
    android
    log
    oboe::oboe
)
