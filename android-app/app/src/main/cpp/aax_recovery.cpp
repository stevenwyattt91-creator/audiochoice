#include <jni.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <thread>
#include <vector>

namespace {
constexpr std::array<uint8_t, 16> fixed_key = {
    0x77,0x21,0x4d,0x4b,0x19,0x6a,0x87,0xcd,
    0x52,0x00,0x45,0xfd,0x20,0xa5,0x1d,0x67
};

inline uint32_t rol(uint32_t value, unsigned bits) {
    return (value << bits) | (value >> (32 - bits));
}

void sha1(const uint8_t* data, size_t length, uint8_t output[20]) {
    uint8_t block[64]{};
    std::memcpy(block, data, length);
    block[length] = 0x80;
    const uint64_t bit_length = static_cast<uint64_t>(length) * 8;
    for (int i = 0; i < 8; ++i) block[63 - i] = static_cast<uint8_t>(bit_length >> (i * 8));

    uint32_t w[80];
    for (int i = 0; i < 16; ++i) {
        w[i] = (static_cast<uint32_t>(block[i * 4]) << 24) |
               (static_cast<uint32_t>(block[i * 4 + 1]) << 16) |
               (static_cast<uint32_t>(block[i * 4 + 2]) << 8) |
               block[i * 4 + 3];
    }
    for (int i = 16; i < 80; ++i) w[i] = rol(w[i-3] ^ w[i-8] ^ w[i-14] ^ w[i-16], 1);

    uint32_t a=0x67452301, b=0xefcdab89, c=0x98badcfe, d=0x10325476, e=0xc3d2e1f0;
    for (int i = 0; i < 80; ++i) {
        uint32_t f, k;
        if (i < 20) { f = (b & c) | ((~b) & d); k = 0x5a827999; }
        else if (i < 40) { f = b ^ c ^ d; k = 0x6ed9eba1; }
        else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8f1bbcdc; }
        else { f = b ^ c ^ d; k = 0xca62c1d6; }
        const uint32_t temp = rol(a, 5) + f + e + k + w[i];
        e=d; d=c; c=rol(b, 30); b=a; a=temp;
    }
    const uint32_t digest[5] = {
        0x67452301 + a, 0xefcdab89 + b, 0x98badcfe + c,
        0x10325476 + d, 0xc3d2e1f0 + e
    };
    for (int i = 0; i < 5; ++i) {
        output[i*4] = static_cast<uint8_t>(digest[i] >> 24);
        output[i*4+1] = static_cast<uint8_t>(digest[i] >> 16);
        output[i*4+2] = static_cast<uint8_t>(digest[i] >> 8);
        output[i*4+3] = static_cast<uint8_t>(digest[i]);
    }
}

bool matches(uint32_t candidate, const uint8_t expected[20]) {
    uint8_t activation[4] = {
        static_cast<uint8_t>(candidate >> 24), static_cast<uint8_t>(candidate >> 16),
        static_cast<uint8_t>(candidate >> 8), static_cast<uint8_t>(candidate)
    };
    uint8_t first[20], second[20], calculated[20];
    uint8_t input1[20];
    std::memcpy(input1, fixed_key.data(), 16); std::memcpy(input1 + 16, activation, 4);
    sha1(input1, sizeof(input1), first);
    uint8_t input2[40];
    std::memcpy(input2, fixed_key.data(), 16); std::memcpy(input2 + 16, first, 20);
    std::memcpy(input2 + 36, activation, 4); sha1(input2, sizeof(input2), second);
    uint8_t input3[32];
    std::memcpy(input3, first, 16); std::memcpy(input3 + 16, second, 16);
    sha1(input3, sizeof(input3), calculated);
    return std::memcmp(calculated, expected, 20) == 0;
}
}

extern "C" JNIEXPORT jlong JNICALL
Java_com_audiochoice_mobile_importing_NativeAaxRecoveryEngine_nativeSearchRange(
    JNIEnv* env, jobject, jbyteArray checksum, jlong start, jlong end, jint requested_threads) {
    if (!checksum || env->GetArrayLength(checksum) != 20 || start < 0 || end < start ||
        end > 0xffffffffLL) return -1;
    uint8_t expected[20];
    env->GetByteArrayRegion(checksum, 0, 20, reinterpret_cast<jbyte*>(expected));
    const unsigned threads = static_cast<unsigned>(requested_threads > 0 ? requested_threads : 1);
    std::atomic<int64_t> found{-1};
    std::vector<std::thread> workers;
    workers.reserve(threads);
    for (unsigned index = 0; index < threads; ++index) {
        workers.emplace_back([=, &found] {
            for (uint64_t value = static_cast<uint64_t>(start) + index;
                 value <= static_cast<uint64_t>(end) && found.load(std::memory_order_relaxed) < 0;
                 value += threads) {
                if (matches(static_cast<uint32_t>(value), expected)) {
                    found.store(static_cast<int64_t>(value), std::memory_order_relaxed);
                    break;
                }
                if (UINT64_MAX - value < threads) break;
            }
        });
    }
    for (auto& worker : workers) worker.join();
    return static_cast<jlong>(found.load());
}
