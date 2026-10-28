// Check real client assets with the client's exact miniaudio version/configuration.
// No playback device is opened and no sound is played.
#define MINIAUDIO_IMPLEMENTATION
#include "../../Lib/Own/Engine Lib/Engine Lib/TMiniAudio.h"

#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

static void Require(ma_result result, const char* operation)
{
    if (result != MA_SUCCESS)
        throw std::runtime_error(std::string(operation) + ": " + std::to_string(result));
}

struct Decoder
{
    ma_decoder value{};
    bool initialized = false;
    ~Decoder() { if (initialized) ma_decoder_uninit(&value); }
};

struct Sound
{
    ma_sound value{};
    bool initialized = false;
    ~Sound() { if (initialized) ma_sound_uninit(&value); }
};

int main(int argc, char** argv)
{
    if (argc != 3) {
        std::cerr << "Usage: MiniaudioCheck <type-tab-absolute-path-list.tsv> <NEW-report.tsv>\n";
        return 2;
    }
    std::ifstream input(argv[1]);
    std::ifstream existing(argv[2]);
    if (!input || existing) { std::cerr << "Missing input or report already exists.\n"; return 2; }
    std::ofstream report(argv[2]);
    if (!report) return 2;
    report << "Path\tMediaType\tResult\tChannels\tSampleRate\tDecodedFrames\tDeclaredFrames\tPeak\tLoader\n";

    auto engine = std::make_unique<ma_engine>();
    auto engineConfig = ma_engine_config_init();
    engineConfig.noDevice = MA_TRUE;
    engineConfig.channels = 2;
    engineConfig.sampleRate = 48000;
    const ma_result engineResult = ma_engine_init(&engineConfig, engine.get());
    if (engineResult != MA_SUCCESS) {
        std::cerr << "Offline engine init failed: " << engineResult << '\n';
        return 1;
    }

    unsigned total = 0, failed = 0;
    std::string line;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty()) continue;
        const auto separator = line.find('\t');
        if (separator == std::string::npos) { ++failed; continue; }
        const std::string path = line.substr(separator + 1);
        unsigned channels = 0, rate = 0;
        ma_uint64 frames = 0, length = 0;
        float peak = 0;
        std::string result = "OK", loader;
        int type = -1;
        try {
            type = std::stoi(line.substr(0, separator));
            if (type < 0 || type > 2) throw std::runtime_error("Unknown media type");
            Decoder decoder;
            auto config = ma_decoder_config_init(ma_format_f32, 0, 0);
            Require(ma_decoder_init_file(path.c_str(), &config, &decoder.value), "decoder init");
            decoder.initialized = true;
            channels = decoder.value.outputChannels;
            rate = decoder.value.outputSampleRate;
            if (channels == 0 || channels > 32 || rate == 0) throw std::runtime_error("Invalid format");
            Require(ma_decoder_get_length_in_pcm_frames(&decoder.value, &length), "length");
            Require(ma_decoder_seek_to_pcm_frame(&decoder.value, 0), "rewind");
            std::vector<float> buffer(4096 * channels);
            for (;;) {
                ma_uint64 read = 0;
                const ma_result status = ma_decoder_read_pcm_frames(&decoder.value, buffer.data(), 4096, &read);
                if (status != MA_SUCCESS && status != MA_AT_END) Require(status, "decode");
                for (size_t i = 0; i < read * channels; ++i) {
                    if (!std::isfinite(buffer[i])) throw std::runtime_error("Non-finite PCM sample");
                    peak = (std::max)(peak, std::abs(buffer[i]));
                }
                frames += read;
                if (read == 0) break;
            }
            if (frames == 0 || frames != length) throw std::runtime_error("Empty/truncated decoded stream");
            Require(ma_decoder_seek_to_pcm_frame(&decoder.value, frames / 2), "seek midpoint");
            ma_uint64 read = 0;
            Require(ma_decoder_read_pcm_frames(&decoder.value, buffer.data(), 1, &read), "read after seek");
            if (read != 1) throw std::runtime_error("Seek did not produce audio");

            // These are the flags used by CD3DSound::LoadData and CT3DMusic::Lock.
            // Type 0 is DirectShow in the client: only its audio decoder is checked here.
            if (type != 0) {
                Sound sound;
                const ma_uint32 flags = type == 1 ? MA_SOUND_FLAG_DECODE : MA_SOUND_FLAG_STREAM | MA_SOUND_FLAG_NO_SPATIALIZATION;
                Require(ma_sound_init_from_file(engine.get(), path.c_str(), flags, nullptr, nullptr, &sound.value), "client sound loader");
                sound.initialized = true;
                Require(ma_sound_seek_to_pcm_frame(&sound.value, 0), "sound rewind");
                if (type == 1) {
                    Sound copy;
                    Require(ma_sound_init_copy(engine.get(), &sound.value, 0, nullptr, &copy.value), "sound copy");
                    copy.initialized = true;
                }
                loader = type == 1 ? "decode+copy" : "stream";
            } else loader = "decoder-only (client uses DirectShow)";
        } catch (const std::exception& error) {
            result = error.what();
            ++failed;
            std::cerr << path << ": " << result << '\n';
        }
        ++total;
        report << path << '\t' << type << '\t' << result << '\t' << channels << '\t' << rate << '\t'
               << frames << '\t' << length << '\t' << peak << '\t' << loader << '\n';
        if (total % 100 == 0) std::cout << "Checked " << total << " files; failures " << failed << std::endl;
    }
    ma_engine_uninit(engine.get());
    report.flush();
    std::cout << "Checked " << total << " files; failures " << failed << std::endl;
    return failed || !report || total == 0 ? 1 : 0;
}
