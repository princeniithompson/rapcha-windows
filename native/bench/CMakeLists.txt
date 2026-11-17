# CI only (never shipped): a tiny 32-bit program that loads rapcha-whisper.dll, a model and a 16 kHz WAV, and prints
# the time taken. It is run under Intel SDE emulating a Goldmont processor (Celeron N3350 class, no AVX).
cmake_minimum_required(VERSION 3.20)
cmake_policy(SET CMP0091 NEW)
project(rw_bench C)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_executable(rw_bench rw_bench.c)
