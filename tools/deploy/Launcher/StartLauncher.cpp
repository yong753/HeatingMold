// HeatingMold.Start.exe — 배포 패키지 시작 도구 (Win32, /MT 정적 CRT)
//
// 이 실행 파일은 VC 런타임(.NET 포함)이 전혀 없는 PC 에서도 시작되어야 한다.
//   - /MT 정적 CRT, C++/CLI·.NET·OCCT 의존 없음
//   - asInvoker (관리자 권한 요청 없음)
//
// 역할
//   1. 패키지 구조 확인 (App\HeatingMold.App.exe, App\deployment.json)
//   2. manifest.sha256 으로 파일 무결성 검사
//   3. Windows 10 x64 확인
//   4. App\HeatingMold.App.exe 를 같은 폴더를 작업 디렉터리로 실행
//
// 실패는 원인별로 구분해 안내하고 %LOCALAPPDATA%\HeatingMold\Logs\launcher.log 에 남긴다.
// 앱 자체를 관리자 권한으로 올리지 않는다.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <cstdio>
#include <cctype>
#include <string.h>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "shell32.lib")

namespace
{
    const wchar_t* kProductFolder = L"HeatingMold";
    const wchar_t* kAppRelativeExe = L"App\\HeatingMold.App.exe";
    const wchar_t* kAppRelativeManifest = L"App\\deployment.json";
    const wchar_t* kManifestFile = L"manifest.sha256";

    std::wstring g_baseDir;
    std::wstring g_logPath;
    std::vector<std::wstring> g_problems;
    bool g_quiet = false;

    std::wstring GetBaseDirectory()
    {
        std::vector<wchar_t> buffer(32768);
        DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0 || length >= buffer.size()) return std::wstring();
        std::wstring path(buffer.data(), length);
        size_t slash = path.find_last_of(L"\\/");
        if (slash == std::wstring::npos) return std::wstring();
        return path.substr(0, slash);
    }

    std::wstring GetLogPath()
    {
        std::vector<wchar_t> buffer(32768);
        DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer.data(), static_cast<DWORD>(buffer.size()));
        std::wstring root;
        if (length == 0 || length >= buffer.size())
        {
            wchar_t temp[MAX_PATH + 1];
            DWORD tempLength = GetTempPathW(MAX_PATH + 1, temp);
            root = (tempLength > 0) ? std::wstring(temp, tempLength) : std::wstring(L".");
        }
        else
        {
            root.assign(buffer.data(), length);
        }

        std::wstring dir = root + L"\\" + kProductFolder + L"\\Logs";
        CreateDirectoryW((root + L"\\" + kProductFolder).c_str(), nullptr);
        CreateDirectoryW(dir.c_str(), nullptr);
        return dir + L"\\launcher.log";
    }

    void WriteLog(const std::wstring& message)
    {
        if (g_logPath.empty()) g_logPath = GetLogPath();

        SYSTEMTIME st;
        GetLocalTime(&st);
        wchar_t stamp[64];
        swprintf_s(stamp, L"%04u-%02u-%02u %02u:%02u:%02u.%03u ",
                   st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

        // UTF-8 로 기록한다. 메모장/에디터에서 한글이 깨지지 않게 하기 위함.
        std::wstring line = std::wstring(stamp) + message + L"\r\n";
        int size = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (size <= 0) return;
        std::string utf8(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), &utf8[0], size, nullptr, nullptr);

        HANDLE file = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(file);
    }

    void AddProblem(const std::wstring& problem)
    {
        g_problems.push_back(problem);
        WriteLog(L"[문제] " + problem);
    }

    bool FileExists(const std::wstring& path)
    {
        DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    bool DirectoryExists(const std::wstring& path)
    {
        DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    bool ComputeSha256(const std::wstring& path, std::string& hex, std::wstring& error)
    {
        hex.clear();
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            error = L"파일을 열지 못했습니다 (오류 " + std::to_wstring(GetLastError()) + L")";
            return false;
        }

        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        std::vector<BYTE> hashObject;
        std::vector<BYTE> digest(32);
        bool ok = false;

        do
        {
            if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0)))
            {
                error = L"SHA-256 공급자를 열지 못했습니다";
                break;
            }

            DWORD objectSize = 0, resultSize = 0;
            if (!BCRYPT_SUCCESS(BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                                                  reinterpret_cast<PUCHAR>(&objectSize), sizeof(objectSize), &resultSize, 0)))
            {
                error = L"SHA-256 속성을 읽지 못했습니다";
                break;
            }
            hashObject.resize(objectSize);

            if (!BCRYPT_SUCCESS(BCryptCreateHash(algorithm, &hash, hashObject.data(), objectSize, nullptr, 0, 0)))
            {
                error = L"SHA-256 해시를 만들지 못했습니다";
                break;
            }

            const DWORD kChunk = 128 * 1024;
            std::vector<BYTE> chunk(kChunk);
            for (;;)
            {
                DWORD read = 0;
                if (!ReadFile(file, chunk.data(), kChunk, &read, nullptr))
                {
                    error = L"파일을 읽지 못했습니다 (오류 " + std::to_wstring(GetLastError()) + L")";
                    break;
                }
                if (read == 0) break;
                if (!BCRYPT_SUCCESS(BCryptHashData(hash, chunk.data(), read, 0)))
                {
                    error = L"해시를 계산하지 못했습니다";
                    break;
                }
            }
            if (!error.empty()) break;

            if (!BCRYPT_SUCCESS(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0)))
            {
                error = L"해시 결과를 얻지 못했습니다";
                break;
            }

            static const wchar_t* kHex = L"0123456789abcdef";
            hex.resize(digest.size() * 2);
            for (size_t i = 0; i < digest.size(); ++i)
            {
                hex[i * 2] = static_cast<char>(kHex[digest[i] >> 4]);
                hex[i * 2 + 1] = static_cast<char>(kHex[digest[i] & 0x0F]);
            }
            ok = true;
        } while (false);

        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        CloseHandle(file);
        return ok;
    }

    std::wstring Trim(const std::wstring& value)
    {
        size_t begin = value.find_first_not_of(L" \t\r\n");
        if (begin == std::wstring::npos) return std::wstring();
        size_t end = value.find_last_not_of(L" \t\r\n");
        return value.substr(begin, end - begin + 1);
    }

    // manifest.sha256: "<sha256 hex>  <패키지 기준 상대 경로>" 한 줄씩
    bool VerifyManifest()
    {
        std::wstring manifestPath = g_baseDir + L"\\" + kManifestFile;
        if (!FileExists(manifestPath))
        {
            AddProblem(L"manifest.sha256 파일이 없습니다. 압축을 다시 풀어 주세요.");
            return false;
        }

        HANDLE file = CreateFileW(manifestPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            AddProblem(L"manifest.sha256 파일을 열지 못했습니다.");
            return false;
        }

        std::string raw;
        {
            const DWORD kChunk = 64 * 1024;
            std::vector<char> chunk(kChunk);
            for (;;)
            {
                DWORD read = 0;
                if (!ReadFile(file, chunk.data(), kChunk, &read, nullptr) || read == 0) break;
                raw.append(chunk.data(), read);
            }
        }
        CloseHandle(file);

        int wideLength = MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), static_cast<int>(raw.size()), nullptr, 0);
        std::wstring text;
        if (wideLength > 0)
        {
            text.resize(static_cast<size_t>(wideLength));
            MultiByteToWideChar(CP_UTF8, 0, raw.c_str(), static_cast<int>(raw.size()), &text[0], wideLength);
        }
        if (text.empty())
        {
            AddProblem(L"manifest.sha256 파일이 비어 있습니다.");
            return false;
        }

        size_t cursor = 0;
        int checked = 0;
        int failures = 0;
        while (cursor <= text.size())
        {
            size_t lineEnd = text.find(L'\n', cursor);
            std::wstring line = (lineEnd == std::wstring::npos)
                                    ? text.substr(cursor)
                                    : text.substr(cursor, lineEnd - cursor);
            cursor = (lineEnd == std::wstring::npos) ? text.size() + 1 : lineEnd + 1;

            line = Trim(line);
            if (line.size() < 66) continue;

            std::wstring expected = line.substr(0, 64);
            std::wstring relative = Trim(line.substr(64));
            if (!relative.empty() && (relative[0] == L'*' || relative[0] == L' '))
                relative = Trim(relative.substr(1));
            if (relative.empty()) continue;

            std::wstring full = g_baseDir + L"\\" + relative;
            if (!FileExists(full))
            {
                AddProblem(L"파일이 없습니다: " + relative);
                ++failures;
                continue;
            }

            std::string actual;
            std::wstring error;
            if (!ComputeSha256(full, actual, error))
            {
                AddProblem(L"해시를 계산하지 못했습니다: " + relative + L" — " + error);
                ++failures;
                continue;
            }

            std::string expectedNarrow;
            expectedNarrow.reserve(expected.size());
            for (wchar_t wc : expected) expectedNarrow.push_back(static_cast<char>(wc));
            for (char& c : expectedNarrow) c = static_cast<char>(::tolower(static_cast<unsigned char>(c)));
            if (actual != expectedNarrow)
            {
                AddProblem(L"파일이 변조되었거나 손상되었습니다: " + relative);
                ++failures;
            }
            ++checked;
        }

        if (checked == 0 && failures == 0)
        {
            AddProblem(L"manifest.sha256 에 검사할 파일이 없습니다.");
            return false;
        }

        WriteLog(L"무결성 검사: " + std::to_wstring(checked) + L"개 확인, 실패 " + std::to_wstring(failures) + L"개");
        return failures == 0;
    }

    bool CheckWindowsVersion()
    {
        // Windows 10 (10.0.10240) 이상. 에디션/서비스 상태는 여기서 판정하지 않는다.
        typedef LONG(WINAPI * RtlGetVersionPtr)(PRTL_OSVERSIONINFOW);
        HMODULE module = GetModuleHandleW(L"ntdll.dll");
        if (module == nullptr) return true;
        RtlGetVersionPtr getVersion = reinterpret_cast<RtlGetVersionPtr>(
            reinterpret_cast<void*>(GetProcAddress(module, "RtlGetVersion")));
        if (getVersion == nullptr) return true;

        RTL_OSVERSIONINFOW info = {};
        info.dwOSVersionInfoSize = sizeof(info);
        if (getVersion(&info) != 0) return true;

        WriteLog(L"Windows 버전: " + std::to_wstring(info.dwMajorVersion) + L"." +
                 std::to_wstring(info.dwMinorVersion) + L"." + std::to_wstring(info.dwBuildNumber));

        if (info.dwMajorVersion < 10)
        {
            AddProblem(L"Windows 10 이상이 필요합니다. 현재 버전: " +
                       std::to_wstring(info.dwMajorVersion) + L"." + std::to_wstring(info.dwMinorVersion));
            return false;
        }
        return true;
    }

    bool CheckArchitecture()
    {
        SYSTEM_INFO info = {};
        GetNativeSystemInfo(&info);
        if (info.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64)
        {
            AddProblem(L"64비트 Windows(x64)에서만 실행됩니다.");
            return false;
        }
        return true;
    }

    // 명령줄에서 이 실행 파일 경로 토큰만 제거하고 나머지를 그대로 넘긴다.
    std::wstring GetForwardArguments()
    {
        const wchar_t* commandLine = GetCommandLineW();
        if (commandLine == nullptr) return std::wstring();

        const wchar_t* p = commandLine;
        if (*p == L'"')
        {
            ++p;
            while (*p && *p != L'"') ++p;
            if (*p == L'"') ++p;
        }
        else
        {
            while (*p && *p != L' ' && *p != L'\t') ++p;
        }
        while (*p == L' ' || *p == L'\t') ++p;
        return std::wstring(p);
    }

    bool HasFlag(const std::wstring& args, const wchar_t* flag)
    {
        return args.find(flag) != std::wstring::npos;
    }

    // 시작 도구 전용 인자는 앱에 넘기지 않는다. 앱은 모르는 인자를 받으면 안 된다.
    std::wstring StripLauncherFlags(const std::wstring& args)
    {
        static const wchar_t* kFlags[] = { L"--check-only", L"--diagnostics", L"--skip-verify", L"--quiet" };

        std::wstring result;
        size_t i = 0;
        while (i < args.size())
        {
            while (i < args.size() && (args[i] == L' ' || args[i] == L'\t')) ++i;
            if (i >= args.size()) break;

            size_t start = i;
            bool quoted = false;
            if (args[i] == L'"') { quoted = true; ++i; }
            while (i < args.size())
            {
                if (quoted) { if (args[i] == L'"') { ++i; break; } }
                else if (args[i] == L' ' || args[i] == L'\t') break;
                ++i;
            }

            std::wstring token = args.substr(start, i - start);
            std::wstring bare = token;
            if (quoted && bare.size() >= 2 && bare.front() == L'"' && bare.back() == L'"')
                bare = bare.substr(1, bare.size() - 2);

            bool skip = false;
            for (const wchar_t* flag : kFlags)
            {
                if (_wcsicmp(bare.c_str(), flag) == 0) { skip = true; break; }
            }
            if (skip) continue;

            if (!result.empty()) result.push_back(L' ');
            result += token;
        }
        return result;
    }

    void ShowMessage(const std::wstring& text, UINT type)
    {
        WriteLog(L"[안내] " + text);
        if (g_quiet) return;
        MessageBoxW(nullptr, text.c_str(), L"HeatingMold 테스트 버전", MB_OK | type);
    }

    std::wstring BuildProblemText(const std::wstring& header)
    {
        std::wstring text = header + L"\n";
        int shown = 0;
        for (const std::wstring& problem : g_problems)
        {
            if (shown >= 8)
            {
                text += L"  … 그 외 " + std::to_wstring(g_problems.size() - shown) + L"건\n";
                break;
            }
            text += L"  - " + problem + L"\n";
            ++shown;
        }
        text += L"\n진단 로그: " + (g_logPath.empty() ? GetLogPath() : g_logPath);
        return text;
    }
}

int wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    g_baseDir = GetBaseDirectory();
    g_logPath = GetLogPath();

    std::wstring args = GetForwardArguments();
    bool checkOnly = HasFlag(args, L"--check-only");
    bool diagnostics = HasFlag(args, L"--diagnostics");
    bool skipVerify = HasFlag(args, L"--skip-verify");
    g_quiet = HasFlag(args, L"--quiet");

    WriteLog(L"=== 시작 도구 실행 ===");
    WriteLog(L"패키지 폴더: " + g_baseDir);

    if (g_baseDir.empty())
    {
        ShowMessage(L"시작 도구의 위치를 확인하지 못했습니다.", MB_ICONERROR);
        return 2;
    }

    if (diagnostics)
    {
        std::wstring dir = g_logPath.substr(0, g_logPath.find_last_of(L"\\/"));
        WriteLog(L"진단 폴더 열기: " + dir);
        ShellExecuteW(nullptr, L"open", dir.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        return 0;
    }

    if (!CheckArchitecture()) { ShowMessage(BuildProblemText(L"이 PC 에서 실행할 수 없습니다."), MB_ICONERROR); return 3; }
    if (!CheckWindowsVersion()) { ShowMessage(BuildProblemText(L"지원되지 않는 Windows 버전입니다."), MB_ICONERROR); return 3; }

    std::wstring appExe = g_baseDir + L"\\" + kAppRelativeExe;
    std::wstring appManifest = g_baseDir + L"\\" + kAppRelativeManifest;

    if (!DirectoryExists(g_baseDir + L"\\App"))
        AddProblem(L"App 폴더가 없습니다. 압축을 다시 풀어 주세요.");
    if (!FileExists(appExe))
        AddProblem(L"App\\HeatingMold.App.exe 가 없습니다. 압축을 다시 풀어 주세요.");
    if (!FileExists(appManifest))
        AddProblem(L"App\\deployment.json 이 없습니다. 배포 패키지가 불완전합니다.");

    if (g_problems.empty() && !skipVerify)
    {
        VerifyManifest();
    }
    else if (skipVerify)
    {
        WriteLog(L"[경고] --skip-verify 로 무결성 검사를 건너뜁니다.");
    }

    if (!g_problems.empty())
    {
        ShowMessage(BuildProblemText(L"배포 패키지를 확인하지 못했습니다."), MB_ICONERROR);
        return 4;
    }

    if (checkOnly)
    {
        WriteLog(L"검사 완료 (앱을 실행하지 않음).");
        ShowMessage(L"패키지 확인이 끝났습니다.\n\n" + std::wstring(L"앱 폴더: ") + g_baseDir +
                    L"\n진단 로그: " + g_logPath, MB_ICONINFORMATION);
        return 0;
    }

    std::wstring appDirectory = g_baseDir + L"\\App";
    std::wstring forwardArgs = StripLauncherFlags(args);
    std::wstring commandLine = L"\"" + appExe + L"\"";
    if (!forwardArgs.empty()) commandLine += L" " + forwardArgs;

    WriteLog(L"앱 실행: " + commandLine);

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};

    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');

    BOOL created = CreateProcessW(appExe.c_str(), mutableCommand.data(), nullptr, nullptr, FALSE,
                                  0, nullptr, appDirectory.c_str(), &startup, &process);
    if (!created)
    {
        DWORD error = GetLastError();
        AddProblem(L"HeatingMold.App.exe 를 실행하지 못했습니다 (오류 " + std::to_wstring(error) + L").");
        ShowMessage(BuildProblemText(L"프로그램을 시작하지 못했습니다."), MB_ICONERROR);
        return 5;
    }

    CloseHandle(process.hThread);

    // 앱 자체의 종료 코드는 사용자가 창을 닫은 것일 수 있으므로 실패로 단정하지 않는다.
    DWORD waitResult = WaitForSingleObject(process.hProcess, 15000);
    if (waitResult == WAIT_OBJECT_0)
    {
        DWORD exitCode = 0;
        if (GetExitCodeProcess(process.hProcess, &exitCode))
        {
            WriteLog(L"앱이 빠르게 종료되었습니다. 종료 코드: " + std::to_wstring(exitCode));
            if (exitCode != 0)
            {
                CloseHandle(process.hProcess);
                std::wstring text = L"프로그램이 시작 직후 종료되었습니다 (종료 코드 " +
                                    std::to_wstring(exitCode) + L").\n\n진단 로그: " + g_logPath;
                ShowMessage(text, MB_ICONWARNING);
                return 6;
            }
        }
    }
    else
    {
        WriteLog(L"앱이 실행 중입니다.");
    }

    CloseHandle(process.hProcess);
    WriteLog(L"시작 도구 종료.");
    return 0;
}
