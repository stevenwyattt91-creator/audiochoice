#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ANDROID_PROJECT="$PROJECT_ROOT/android-app"
LOCAL_PROPERTIES="$ANDROID_PROJECT/local.properties"
FFMPEG_VERSION="8.1.2"
BUILD_REVISION="2-portable-pic"
FFMPEG_ARCHIVE="ffmpeg-${FFMPEG_VERSION}.tar.xz"
FFMPEG_URL="https://ffmpeg.org/releases/${FFMPEG_ARCHIVE}"
WORK_ROOT="$PROJECT_ROOT/work/ffmpeg-android"
SOURCE_ROOT="$WORK_ROOT/ffmpeg-${FFMPEG_VERSION}"
OUTPUT_ROOT="$ANDROID_PROJECT/app/src/main/ffmpeg"
JNI_ROOT="$ANDROID_PROJECT/app/src/main/jniLibs"

if [[ ! -f "$LOCAL_PROPERTIES" ]]; then
  echo "Android Studio local.properties was not found. Open the Android project once, then retry."
  exit 1
fi

SDK_ROOT="$(sed -n 's/^sdk.dir=//p' "$LOCAL_PROPERTIES" | tail -1 | sed 's/\\:/:/g; s/\\\\/\\/g')"
NDK_ROOT="$SDK_ROOT/ndk/30.0.15729638"
TOOLCHAIN="$NDK_ROOT/toolchains/llvm/prebuilt/darwin-x86_64"
if [[ ! -d "$TOOLCHAIN" ]]; then
  TOOLCHAIN="$NDK_ROOT/toolchains/llvm/prebuilt/darwin-arm64"
fi
if [[ ! -d "$TOOLCHAIN" ]]; then
  echo "Android NDK 30.0.15729638 was not found under $SDK_ROOT."
  exit 1
fi

mkdir -p "$WORK_ROOT" "$OUTPUT_ROOT" "$JNI_ROOT"
if [[ ! -f "$WORK_ROOT/$FFMPEG_ARCHIVE" ]]; then
  echo "Downloading official FFmpeg ${FFMPEG_VERSION} source..."
  curl --fail --location "$FFMPEG_URL" --output "$WORK_ROOT/$FFMPEG_ARCHIVE"
fi
if [[ ! -d "$SOURCE_ROOT" ]]; then
  tar -xf "$WORK_ROOT/$FFMPEG_ARCHIVE" -C "$WORK_ROOT"
fi

build_abi() {
  local abi="$1"
  local ff_arch="$2"
  local clang_prefix="$3"
  local cpu="$4"
  local build_dir="$WORK_ROOT/build-$abi"
  local prefix="$OUTPUT_ROOT/$abi"
  local revision_file="$prefix/.audiochoice-build-revision"

  if [[ -f "$JNI_ROOT/$abi/libavformat.a" && \
        -f "$JNI_ROOT/$abi/libavcodec.a" && \
        -f "$JNI_ROOT/$abi/libavutil.a" && \
        -f "$revision_file" && \
        "$(cat "$revision_file")" == "$BUILD_REVISION" ]]; then
    echo "Reusing completed FFmpeg libraries for $abi."
    return
  fi
  rm -rf "$build_dir" "$prefix"
  mkdir -p "$build_dir" "$prefix" "$JNI_ROOT/$abi"
  pushd "$build_dir" >/dev/null
  "$SOURCE_ROOT/configure" \
    --prefix="$prefix" \
    --target-os=android \
    --arch="$ff_arch" \
    --cpu="$cpu" \
    --cc="$TOOLCHAIN/bin/${clang_prefix}26-clang" \
    --cxx="$TOOLCHAIN/bin/${clang_prefix}26-clang++" \
    --ar="$TOOLCHAIN/bin/llvm-ar" \
    --ranlib="$TOOLCHAIN/bin/llvm-ranlib" \
    --strip="$TOOLCHAIN/bin/llvm-strip" \
    --enable-cross-compile \
    --enable-pic \
    --disable-asm \
    --enable-small \
    --disable-programs \
    --disable-doc \
    --disable-debug \
    --disable-network \
    --disable-autodetect \
    --disable-everything \
    --enable-avutil \
    --enable-avcodec \
    --enable-avformat \
    --enable-protocol=file \
    --enable-demuxer=mov \
    --enable-muxer=mp4 \
    --enable-parser=aac
  make -j"$(sysctl -n hw.logicalcpu 2>/dev/null || echo 4)"
  make install
  popd >/dev/null

  cp "$prefix/lib/libavformat.a" "$JNI_ROOT/$abi/"
  cp "$prefix/lib/libavcodec.a" "$JNI_ROOT/$abi/"
  cp "$prefix/lib/libavutil.a" "$JNI_ROOT/$abi/"
  printf '%s\n' "$BUILD_REVISION" > "$revision_file"
}

build_abi "arm64-v8a" "aarch64" "aarch64-linux-android" "armv8-a"
build_abi "x86_64" "x86_64" "x86_64-linux-android" "x86-64"

echo "FFmpeg Android libraries built successfully for arm64-v8a and x86_64."
