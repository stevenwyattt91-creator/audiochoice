cmake_minimum_required(VERSION 3.22.1)
project(audiochoice_aax LANGUAGES CXX)

add_library(audiochoice_aax SHARED aax_recovery.cpp aax_remuxer.cpp)
target_compile_features(audiochoice_aax PRIVATE cxx_std_20)
target_compile_options(audiochoice_aax PRIVATE -O3 -fvisibility=hidden)

set(ffmpeg_root "${CMAKE_CURRENT_SOURCE_DIR}/../ffmpeg/${ANDROID_ABI}")
set(ffmpeg_archives "${CMAKE_CURRENT_SOURCE_DIR}/../jniLibs/${ANDROID_ABI}")
foreach(ffmpeg_library avformat avcodec avutil)
    add_library(${ffmpeg_library} STATIC IMPORTED)
    set_target_properties(${ffmpeg_library} PROPERTIES
        IMPORTED_LOCATION "${ffmpeg_archives}/lib${ffmpeg_library}.a")
endforeach()

target_include_directories(audiochoice_aax PRIVATE "${ffmpeg_root}/include")
target_link_libraries(audiochoice_aax PRIVATE avformat avcodec avutil z m log android atomic)
