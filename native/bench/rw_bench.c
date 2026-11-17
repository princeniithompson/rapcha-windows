/* CI only: rw_bench <rapcha-whisper.dll> <model.bin> <audio16k.wav> <threads>
   Prints "load_ms=.. transcribe_ms=.. audio_s=.. rtf=.. chars=.." and the text on the next line. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

typedef void * (__cdecl * load_fn)(const wchar_t *, int);
typedef int (__cdecl * transcribe_fn)(void *, const float *, int, int, const char *, const char *, char *, int);
typedef const char * (__cdecl * info_fn)(void);

static FARPROC get(HMODULE m, const char * n) { FARPROC p = GetProcAddress(m, n); if (!p) { char u[64]; sprintf(u, "_%s", n); p = GetProcAddress(m, u); } return p; }

int wmain(int argc, wchar_t ** argv)
{
    if (argc < 5) { fprintf(stderr, "usage: rw_bench dll model wav threads\n"); return 2; }
    HMODULE m = LoadLibraryW(argv[1]);
    if (!m) { fprintf(stderr, "LoadLibrary failed %lu\n", GetLastError()); return 3; }
    load_fn load = (load_fn) get(m, "rw_load");
    transcribe_fn tr = (transcribe_fn) get(m, "rw_transcribe");
    info_fn info = (info_fn) get(m, "rw_system_info");
    if (!load || !tr) { fprintf(stderr, "functions missing\n"); return 4; }
    if (info) printf("system_info: %s\n", info());
    FILE * f = _wfopen(argv[3], L"rb");
    if (!f) { fprintf(stderr, "no wav\n"); return 5; }
    fseek(f, 0, SEEK_END); long sz = ftell(f); fseek(f, 44, SEEK_SET);
    int n = (int) ((sz - 44) / 2);
    short * pcm = (short *) malloc(n * 2); float * x = (float *) malloc(n * 4);
    fread(pcm, 2, n, f); fclose(f);
    for (int i = 0; i < n; i++) x[i] = pcm[i] / 32768.0f;
    int threads = _wtoi(argv[4]);
    DWORD t0 = GetTickCount();
    void * ctx = load(argv[2], threads);
    DWORD t1 = GetTickCount();
    if (!ctx) { fprintf(stderr, "model load failed\n"); return 6; }
    static char out[8192];
    int r = tr(ctx, x, n, threads, NULL, "en", out, sizeof(out));
    DWORD t2 = GetTickCount();
    double audio = n / 16000.0, ms = (double) (t2 - t1);
    printf("load_ms=%lu transcribe_ms=%.0f audio_s=%.2f rtf=%.2f result=%d chars=%d\n", t1 - t0, ms, audio, ms / 1000.0 / audio, r, r > 0 ? r : 0);
    printf("text: %s\n", r > 0 ? out : "");
    return r >= 0 ? 0 : 7;
}
