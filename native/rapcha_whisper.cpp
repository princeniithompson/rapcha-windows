// Rapcha's tiny C API over whisper.cpp, built into one self-contained DLL (whisper + ggml linked statically,
// static C runtime). Rapcha.exe carries the DLL as an embedded resource, writes it to %AppData%\Rapcha\engine
// and loads it with LoadLibrary. Only plain types cross the boundary (no structs by value), and whisper's own
// logging is switched off so nothing (no words) is printed.
#include "whisper.h"
#include <cstdio>
#include <cstring>
#include <string>

#if defined(_WIN32)
#define RW_API extern "C" __declspec(dllexport)
#else
#define RW_API extern "C" __attribute__((visibility("default")))
#endif

static void rw_quiet(enum ggml_log_level, const char *, void *) {}

struct rw_file { FILE * f; };

static size_t rw_read(void * ctx, void * out, size_t n) { return fread(out, 1, n, ((rw_file *) ctx)->f); }
static bool rw_eof(void * ctx) { return feof(((rw_file *) ctx)->f) != 0; }
static void rw_close(void * ctx) { auto * r = (rw_file *) ctx; if (r->f) fclose(r->f); r->f = nullptr; }

RW_API int rw_version(void) { return 1; }

RW_API const char * rw_system_info(void) { return whisper_print_system_info(); }

// path: UTF-16 on Windows (so a user name with any letters works). Returns null when the model can't be read.
RW_API void * rw_load(const wchar_t * path, int threads_hint)
{
    (void) threads_hint;
    whisper_log_set(rw_quiet, nullptr);
    rw_file file{};
#if defined(_WIN32)
    file.f = _wfopen(path, L"rb");
#else
    std::string p; for (const wchar_t * c = path; *c; ++c) p.push_back((char) *c);
    file.f = fopen(p.c_str(), "rb");
#endif
    if (!file.f) return nullptr;
    whisper_model_loader loader{};
    loader.context = &file;
    loader.read = rw_read;
    loader.eof = rw_eof;
    loader.close = rw_close;
    whisper_context_params cp = whisper_context_default_params();
    cp.use_gpu = false;
    cp.flash_attn = false;
    whisper_context * ctx = whisper_init_with_params(&loader, cp);
    rw_close(&file);
    return ctx;
}

// v19 diagnostics: per segment of the last rw_transcribe: no-speech probability, average token log-probability,
// token count, kept (1) or dropped as non-speech (0), start and end (centiseconds). No text.
static float g_stats[64 * 6];
static int g_nstats = 0;

RW_API int rw_stats(float * out, int cap)
{
    int n = g_nstats * 6 < cap ? g_nstats : cap / 6;
    for (int i = 0; i < n * 6; ++i) out[i] = g_stats[i];
    return g_nstats;
}

// samples: 16 kHz mono float PCM. prompt (UTF-8, may be null): custom vocabulary and the previous piece's text.
// Writes UTF-8 text to out; returns the bytes it needs (excluding the 0), or a negative number on failure.
RW_API int rw_transcribe(void * ctx, const float * samples, int n, int threads, const char * prompt, const char * lang,
                         char * out, int out_cap)
{
    if (!ctx || !samples || n <= 0) return -1;
    whisper_full_params p = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    p.n_threads = threads > 0 ? threads : 2;
    p.translate = false;
    p.no_context = true;
    p.no_timestamps = true;
    // v18 anti-invention settings: one segment for short dictations, no special or non-speech tokens, greedy
    // decoding at temperature 0 with OpenAI's fallback (0.2 steps) when the text repeats (entropy) or is unsure
    // (log-probability), and the no-speech threshold so silence gives no text.
    p.single_segment = n < 16000 * 15;
    p.print_special = false;
    p.print_progress = false;
    p.print_realtime = false;
    p.print_timestamps = false;
    p.suppress_blank = true;
    p.suppress_nst = true;
    p.temperature = 0.0f;
    p.temperature_inc = 0.2f;
    p.entropy_thold = 2.4f;
    p.logprob_thold = -1.0f;
    p.no_speech_thold = 0.6f;
    p.language = (lang && *lang) ? lang : "en";
    p.initial_prompt = (prompt && *prompt) ? prompt : nullptr;
    g_nstats = 0;
    if (whisper_full((whisper_context *) ctx, p, samples, n) != 0) return -2;
    std::string text;
    whisper_context * wc = (whisper_context *) ctx;
    const int segs = whisper_full_n_segments(wc);
    const whisper_token eot = whisper_token_eot(wc);
    g_nstats = segs < 64 ? segs : 64;
    for (int i = 0; i < segs; ++i)
    {
        const float nsp = whisper_full_get_segment_no_speech_prob(wc, i);
        if (i < 64)
        {
            const int nt = whisper_full_n_tokens(wc, i);
            double sum = 0; int cnt = 0;
            for (int j = 0; j < nt; ++j)
            {
                whisper_token_data d = whisper_full_get_token_data(wc, i, j);
                if (d.id >= eot) continue;   // special tokens
                sum += d.plog; ++cnt;
            }
            float * st = g_stats + i * 6;
            st[0] = nsp; st[1] = cnt > 0 ? (float) (sum / cnt) : 0.0f; st[2] = (float) cnt; st[3] = nsp > 0.85f ? 0.0f : 1.0f;
            st[4] = (float) whisper_full_get_segment_t0(wc, i); st[5] = (float) whisper_full_get_segment_t1(wc, i);
        }
        // a segment Whisper itself thinks is not speech is dropped (it is where "thanks for watching" comes from)
        if (nsp > 0.85f) continue;
        const char * t = whisper_full_get_segment_text((whisper_context *) ctx, i);
        if (t) text += t;
    }
    const int need = (int) text.size();
    if (out && out_cap > 0)
    {
        const int m = need < out_cap - 1 ? need : out_cap - 1;
        memcpy(out, text.data(), (size_t) m);
        out[m] = 0;
    }
    return need;
}

RW_API void rw_free(void * ctx) { if (ctx) whisper_free((whisper_context *) ctx); }
