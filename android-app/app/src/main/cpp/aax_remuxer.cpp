#include <jni.h>
#include <cerrno>
#include <cstdio>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/dict.h>
#include <libavutil/error.h>
#include <libavutil/mem.h>
}

namespace {
constexpr int CUSTOM_IO_BUFFER_SIZE = 256 * 1024;

struct AndroidInput {
    int descriptor = -1;
};

int read_android_input(void* opaque, uint8_t* buffer, int size) {
    auto* input = static_cast<AndroidInput*>(opaque);
    const ssize_t count = read(input->descriptor, buffer, static_cast<size_t>(size));
    if (count > 0) return static_cast<int>(count);
    if (count == 0) return AVERROR_EOF;
    return AVERROR(errno);
}

int64_t seek_android_input(void* opaque, int64_t offset, int whence) {
    auto* input = static_cast<AndroidInput*>(opaque);
    if (whence == AVSEEK_SIZE) {
        struct stat details{};
        return fstat(input->descriptor, &details) == 0 ? details.st_size : AVERROR(errno);
    }
    const off_t position = lseek(input->descriptor, static_cast<off_t>(offset), whence & ~AVSEEK_FORCE);
    return position >= 0 ? static_cast<int64_t>(position) : AVERROR(errno);
}

std::string error_text(int code) {
    char buffer[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(code, buffer, sizeof(buffer));
    return buffer;
}

int copy_chapters(const AVFormatContext* input, AVFormatContext* output) {
    if (input->nb_chapters == 0) return 0;
    output->chapters = static_cast<AVChapter**>(
        av_calloc(input->nb_chapters, sizeof(*output->chapters)));
    if (!output->chapters) return AVERROR(ENOMEM);
    output->nb_chapters = input->nb_chapters;
    for (unsigned index = 0; index < input->nb_chapters; ++index) {
        const AVChapter* source = input->chapters[index];
        AVChapter* destination = static_cast<AVChapter*>(av_mallocz(sizeof(*destination)));
        if (!destination) return AVERROR(ENOMEM);
        destination->id = source->id;
        destination->time_base = source->time_base;
        destination->start = source->start;
        destination->end = source->end;
        av_dict_copy(&destination->metadata, source->metadata, 0);
        output->chapters[index] = destination;
    }
    return 0;
}

int remux(
    int input_descriptor,
    const char* output_path,
    const char* cover_output_path,
    uint32_t activation,
    const char** failed_stage) {
    AVFormatContext* input = avformat_alloc_context();
    AVFormatContext* output = nullptr;
    AVIOContext* custom_io = nullptr;
    AVDictionary* input_options = nullptr;
    AVPacket* packet = nullptr;
    AndroidInput android_input{dup(input_descriptor)};
    std::vector<int> stream_mapping;
    int result = 0;
    bool cover_written = false;
    char activation_text[9]{};
    if (!input || android_input.descriptor < 0) {
        result = AVERROR(errno == 0 ? ENOMEM : errno);
        goto cleanup;
    }
    {
        auto* buffer = static_cast<unsigned char*>(av_malloc(CUSTOM_IO_BUFFER_SIZE));
        if (!buffer) {
            result = AVERROR(ENOMEM);
            goto cleanup;
        }
        custom_io = avio_alloc_context(
            buffer,
            CUSTOM_IO_BUFFER_SIZE,
            0,
            &android_input,
            read_android_input,
            nullptr,
            seek_android_input);
        if (!custom_io) {
            av_free(buffer);
            result = AVERROR(ENOMEM);
            goto cleanup;
        }
    }
    custom_io->seekable = AVIO_SEEKABLE_NORMAL;
    input->pb = custom_io;
    input->flags |= AVFMT_FLAG_CUSTOM_IO;
    std::snprintf(activation_text, sizeof(activation_text), "%08X", activation);
    av_dict_set(&input_options, "activation_bytes", activation_text, 0);

    *failed_stage = "opening the authorized AAX input";
    result = avformat_open_input(&input, "input.aax", nullptr, &input_options);
    av_dict_free(&input_options);
    if (result < 0) goto cleanup;
    *failed_stage = "reading the audiobook stream information";
    result = avformat_find_stream_info(input, nullptr);
    if (result < 0) goto cleanup;
    *failed_stage = "creating the M4B container";
    result = avformat_alloc_output_context2(&output, nullptr, "mp4", output_path);
    if (result < 0 || !output) {
        if (result >= 0) result = AVERROR_UNKNOWN;
        goto cleanup;
    }

    av_dict_copy(&output->metadata, input->metadata, 0);
    *failed_stage = "copying audiobook chapters";
    result = copy_chapters(input, output);
    if (result < 0) goto cleanup;
    stream_mapping.assign(input->nb_streams, -1);
    for (unsigned index = 0; index < input->nb_streams; ++index) {
        AVStream* source = input->streams[index];
        if (source->codecpar->codec_type != AVMEDIA_TYPE_AUDIO) continue;
        AVStream* destination = avformat_new_stream(output, nullptr);
        if (!destination) {
            result = AVERROR(ENOMEM);
            goto cleanup;
        }
        result = avcodec_parameters_copy(destination->codecpar, source->codecpar);
        if (result < 0) goto cleanup;
        destination->codecpar->codec_tag = 0;
        destination->time_base = source->time_base;
        destination->disposition = source->disposition;
        av_dict_copy(&destination->metadata, source->metadata, 0);
        stream_mapping[index] = destination->index;
    }
    if (output->nb_streams == 0) {
        result = AVERROR_STREAM_NOT_FOUND;
        goto cleanup;
    }

    *failed_stage = "opening private M4B storage";
    if (!(output->oformat->flags & AVFMT_NOFILE)) {
        result = avio_open(&output->pb, output_path, AVIO_FLAG_WRITE);
        if (result < 0) goto cleanup;
    }
    *failed_stage = "writing the M4B header";
    result = avformat_write_header(output, nullptr);
    if (result < 0) goto cleanup;
    packet = av_packet_alloc();
    if (!packet) {
        result = AVERROR(ENOMEM);
        goto cleanup;
    }
    *failed_stage = "copying encrypted audiobook packets";
    while ((result = av_read_frame(input, packet)) >= 0) {
        if (packet->stream_index < 0 ||
            static_cast<unsigned>(packet->stream_index) >= stream_mapping.size() ||
            stream_mapping[packet->stream_index] < 0) {
            if (!cover_written && packet->stream_index >= 0 &&
                static_cast<unsigned>(packet->stream_index) < input->nb_streams) {
                const AVStream* source = input->streams[packet->stream_index];
                const bool is_cover_art =
                    source->codecpar->codec_type == AVMEDIA_TYPE_VIDEO &&
                    (source->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0;
                if (is_cover_art && cover_output_path && cover_output_path[0] != '\0') {
                    FILE* cover = std::fopen(cover_output_path, "wb");
                    if (cover) {
                        cover_written = std::fwrite(
                            packet->data, 1, static_cast<size_t>(packet->size), cover) ==
                            static_cast<size_t>(packet->size);
                        std::fclose(cover);
                        if (!cover_written) std::remove(cover_output_path);
                    }
                }
            }
            av_packet_unref(packet);
            continue;
        }
        const AVStream* source = input->streams[packet->stream_index];
        packet->stream_index = stream_mapping[packet->stream_index];
        const AVStream* destination = output->streams[packet->stream_index];
        av_packet_rescale_ts(packet, source->time_base, destination->time_base);
        packet->pos = -1;
        result = av_interleaved_write_frame(output, packet);
        av_packet_unref(packet);
        if (result < 0) goto cleanup;
    }
    if (result == AVERROR_EOF) {
        *failed_stage = "finalizing the M4B file";
        result = av_write_trailer(output);
    }

cleanup:
    av_packet_free(&packet);
    if (output && output->pb && !(output->oformat->flags & AVFMT_NOFILE)) avio_closep(&output->pb);
    avformat_free_context(output);
    avformat_close_input(&input);
    if (custom_io) {
        av_freep(&custom_io->buffer);
        avio_context_free(&custom_io);
    }
    if (android_input.descriptor >= 0) close(android_input.descriptor);
    return result;
}

}

extern "C" JNIEXPORT jstring JNICALL
Java_com_audiochoice_mobile_importing_NativeAaxRemuxer_nativeRemux(
    JNIEnv* env, jobject, jint input_descriptor, jstring output_path,
    jstring cover_output_path, jlong activation) {
    if (input_descriptor < 0 || !output_path || !cover_output_path ||
        activation < 0 || activation > 0xffffffffLL) {
        return env->NewStringUTF("Invalid local conversion parameters.");
    }
    const char* output = env->GetStringUTFChars(output_path, nullptr);
    const char* cover_output = env->GetStringUTFChars(cover_output_path, nullptr);
    const char* failed_stage = "starting local conversion";
    const int result = remux(
        input_descriptor,
        output,
        cover_output,
        static_cast<uint32_t>(activation),
        &failed_stage);
    env->ReleaseStringUTFChars(output_path, output);
    env->ReleaseStringUTFChars(cover_output_path, cover_output);
    if (result >= 0) return nullptr;
    const std::string message = "Local M4B conversion failed while " +
        std::string(failed_stage) + ": " + error_text(result);
    return env->NewStringUTF(message.c_str());
}
