#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>
namespace {
std::string utf8(const std::wstring& value) {
    const int count = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string output(static_cast<size_t>(count), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), output.data(), count, nullptr, nullptr); return output;
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 5) return 2;
    const std::wstring output = argv[4];
    if (!CreateDirectoryW(output.c_str(), nullptr)) return 3;
    HMODULE dll = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) return 4;
    auto configure = reinterpret_cast<int(*)(const wchar_t*)>(GetProcAddress(dll, "hdv_provider_configure"));
    auto shutdown = reinterpret_cast<void(*)()>(GetProcAddress(dll, "hdv_provider_shutdown"));
    if (!configure || !shutdown) return 5;
    std::string baseline, master;
    { std::ifstream file(argv[2], std::ios::binary); std::string line; std::getline(file, line); std::getline(file, line); std::getline(file, line);
      if (!line.empty() && line.back() == '\r') line.pop_back(); const auto a = line.find('\t'), b = line.find('\t', a + 1);
      if (a == std::string::npos || b == std::string::npos) return 6;
      baseline = line.substr(a + 1, b - a - 1); master = line.substr(b + 1); }
    std::ofstream log(output + L"\\checks.log"); int checks = 0; bool ok = true;
    auto expect = [&](bool result, const char* label) { ++checks; log << label << '=' << (result ? "PASS" : "FAIL") << '\n'; ok = ok && result; };
    auto file_case = [&](const wchar_t* name, const std::string& body, bool expected) {
        const std::wstring path = output + L"\\" + name;
        { std::ofstream file(path, std::ios::binary); file.write(body.data(), static_cast<std::streamsize>(body.size())); }
        expect((configure(path.c_str()) != 0) == expected, utf8(name).c_str());
    };
    expect(configure(argv[2]) == 1, "valid-existing-config");
    expect(configure((output + L"\\missing.tsv").c_str()) == 0, "missing-config");
    expect(configure(L"relative.tsv") == 0, "relative-config");
    for (const auto* path : {L"C:\\CON", L"C:\\NUL", L"C:\\COM1.txt", L"C:\\LPT9.txt", L"C:\\COM\u00b9", L"\\\\.\\NUL", L"\\\\server\\share\\pack.tsv"})
        expect(configure(path) == 0, "DOS-device-network-path-rejected");
    const std::string valid = "POC_HD_VISIBLE_V1\n1\t1\n1\t" + baseline + '\t' + master + '\n';
    file_case(L"good.tsv", valid, true);
    file_case(L"bad-header.tsv", "BAD\n1\t1\n1\t" + baseline + '\t' + master + '\n', false);
    file_case(L"bad-format.tsv", "POC_HD_VISIBLE_V1\n1\t3\n1\t" + baseline + '\t' + master + '\n', false);
    file_case(L"bad-game.tsv", "POC_HD_VISIBLE_V1\n4\t1\n1\t" + baseline + '\t' + master + '\n', false);
    file_case(L"duplicate-key.tsv", valid + "1\t" + baseline + '\t' + master + '\n', false);
    file_case(L"zero-key.tsv", "POC_HD_VISIBLE_V1\n1\t1\n0\t" + baseline + '\t' + master + '\n', false);
    file_case(L"missing-field.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + baseline + '\n', false);
    file_case(L"missing-ref.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + utf8(output + L"\\missing.bin") + '\t' + master + '\n', false);
    file_case(L"missing-master.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + baseline + '\t' + utf8(output + L"\\missing.png") + '\n', false);
    file_case(L"device-ref.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\tC:\\CON\t" + master + '\n', false);
    file_case(L"device-master.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + baseline + "\tC:\\CON\n", false);
    file_case(L"bad-utf8.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + std::string(1, static_cast<char>(0xc0)) + '\t' + master + '\n', false);
    file_case(L"nul.tsv", valid + std::string(1, '\0'), false);
    file_case(L"large.tsv", std::string(1024 * 1024 + 1, 'a'), false);
    const std::wstring short_path = output + L"\\short.bin", long_path = output + L"\\long.bin";
    { std::ofstream file(short_path, std::ios::binary); const std::string data(115999, 'x'); file.write(data.data(), static_cast<std::streamsize>(data.size())); }
    { std::ofstream file(long_path, std::ios::binary); const std::string data(116001, 'x'); file.write(data.data(), static_cast<std::streamsize>(data.size())); }
    file_case(L"short-ref.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + utf8(short_path) + '\t' + master + '\n', false);
    file_case(L"long-ref.tsv", "POC_HD_VISIBLE_V1\n1\t1\n1\t" + utf8(long_path) + '\t' + master + '\n', false);
    /* Invalid activation must not retain a previously valid engine. */
    expect(configure(argv[2]) == 1 && configure(L"C:\\NUL") == 0, "replacement-failure-clears-previous-config");
    shutdown(); FreeLibrary(dll);
    std::printf("Native config/file admission: %d checks, %s\n", checks, ok ? "PASS" : "FAIL");
    return ok ? 0 : 7;
}
