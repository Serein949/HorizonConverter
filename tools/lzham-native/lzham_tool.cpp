// Ground-truth LZHAM CLI used to validate the C# encoder/decoder ports.
// Build with MSVC (see build.ps1 in this folder). The reference codec lives in
// %TEMP%\lzham-src\lzham_codec-master and is compiled directly into the tool.
//
//   lzham_tool c <in> <out> [dict_log2] [table_rate] [level]
//   lzham_tool d <in> <out> [dict_log2] [table_rate]
//
#include "lzham.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

static bool ReadFile(const char* path, std::vector<unsigned char>& out)
{
    FILE* f = nullptr;
    if (fopen_s(&f, path, "rb") || !f)
        return false;
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    out.resize(len > 0 ? (size_t)len : 0);
    if (!out.empty() && fread(out.data(), 1, out.size(), f) != out.size())
    {
        fclose(f);
        return false;
    }
    fclose(f);
    return true;
}

static bool WriteFile(const char* path, const void* data, size_t len)
{
    FILE* f = nullptr;
    if (fopen_s(&f, path, "wb") || !f)
        return false;
    bool ok = len == 0 || fwrite(data, 1, len, f) == len;
    fclose(f);
    return ok;
}

int main(int argc, char** argv)
{
    if (argc < 4)
    {
        printf("usage: lzham_tool c|d <in> <out> [dict_log2] [table_rate] [level]\n");
        return 2;
    }

    const char* mode = argv[1];
    const unsigned dictLog2 = argc > 4 ? (unsigned)atoi(argv[4]) : 21u;
    const unsigned tableRate = argc > 5 ? (unsigned)atoi(argv[5]) : 20u;
    const unsigned level = argc > 6 ? (unsigned)atoi(argv[6]) : 3u; // LZHAM_COMP_LEVEL_BETTER

    std::vector<unsigned char> in;
    if (!ReadFile(argv[2], in))
    {
        printf("cannot read %s\n", argv[2]);
        return 1;
    }

    if (mode[0] == 'c')
    {
        std::vector<unsigned char> out(in.size() + (in.size() / 2) + 4096);
        size_t outLen = out.size();

        lzham_compress_params params;
        memset(&params, 0, sizeof(params));
        params.m_struct_size = sizeof(params);
        params.m_dict_size_log2 = dictLog2;
        params.m_level = (lzham_compress_level)level;
        params.m_table_update_rate = tableRate;
        params.m_max_helper_threads = 0;

        lzham_compress_status_t st = lzham_compress_memory(&params, out.data(), &outLen, in.data(), in.size(), nullptr);
        if (st != LZHAM_COMP_STATUS_SUCCESS)
        {
            printf("compress failed: %d\n", (int)st);
            return 1;
        }

        if (!WriteFile(argv[3], out.data(), outLen))
        {
            printf("cannot write %s\n", argv[3]);
            return 1;
        }

        printf("compressed %zu -> %zu bytes (%.2fx)\n", in.size(), outLen, in.empty() ? 0.0 : double(in.size()) / double(outLen));
        return 0;
    }

    // decompress: we do not know the exact output size, so grow the buffer until it fits.
    // Aerofly (and our TTX writer) use the zlib-style stream, i.e. a 2-byte header before the
    // LZHAM payload; the reference raw API does not expect it, so detect and strip it here.
    const unsigned char* src = in.data();
    size_t srcLen = in.size();
    if (srcLen > 2 && src[0] == 0x6E && ((int(src[0]) << 8) + src[1]) % 31 == 0)
    {
        src += 2;
        srcLen -= 2;
    }

    size_t cap = srcLen * 8 + (1u << 20);
    for (int attempt = 0; attempt < 6; ++attempt)
    {
        std::vector<unsigned char> out(cap);
        size_t outLen = cap;

        lzham_decompress_params params;
        memset(&params, 0, sizeof(params));
        params.m_struct_size = sizeof(params);
        params.m_dict_size_log2 = dictLog2;
        params.m_table_update_rate = tableRate;

        lzham_decompress_status_t st = lzham_decompress_memory(&params, out.data(), &outLen, src, srcLen, nullptr);
        if (st == LZHAM_DECOMP_STATUS_SUCCESS)
        {
            if (!WriteFile(argv[3], out.data(), outLen))
            {
                printf("cannot write %s\n", argv[3]);
                return 1;
            }
            printf("decompressed %zu -> %zu bytes\n", in.size(), outLen);
            return 0;
        }
        if (st != LZHAM_DECOMP_STATUS_NOT_FINISHED)
        {
            printf("decompress failed: %d\n", (int)st);
            return 1;
        }
        cap *= 4;
    }

    printf("decompress failed: output buffer too small\n");
    return 1;
}
