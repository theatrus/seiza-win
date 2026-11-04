#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <cstdio>
#include <cwchar>

static bool extract_action(const wchar_t* msi)
{
    MSIHANDLE database = 0, view = 0, record = 0;
    if (MsiOpenDatabaseW(msi, MSIDBOPEN_READONLY, &database) != ERROR_SUCCESS) return false;
    if (MsiDatabaseOpenViewW(database, L"SELECT `Data` FROM `Binary` WHERE `Name`='SeizaThumbnailProviderCustomActions'", &view) != ERROR_SUCCESS) return false;
    if (MsiViewExecute(view, 0) != ERROR_SUCCESS || MsiViewFetch(view, &record) != ERROR_SUCCESS) return false;
    HANDLE file = CreateFileW(L"SeizaEmbeddedAction.dll", GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char buffer[8192];
    for (;;) {
        DWORD size = static_cast<DWORD>(sizeof(buffer)), written = 0;
        if (MsiRecordReadStream(record, 1, buffer, &size) != ERROR_SUCCESS) return false;
        if (!size) break;
        if (!WriteFile(file, buffer, size, &written, nullptr) || written != size) return false;
    }
    CloseHandle(file);
    MsiCloseHandle(record);
    MsiCloseHandle(view);
    MsiCloseHandle(database);
    return true;
}

// Built /MT: neither this fresh process nor its DLL-search test needs VC redist.
int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32)) return 1;
    const wchar_t* runtimes[] = {L"vcruntime140.dll", L"vcruntime140_1.dll", L"msvcp140.dll"};
    for (auto name : runtimes) if (GetModuleHandleW(name)) return 2;

    // The script places invalid CRT DLLs beside the process. A /MD control must
    // fail to load, proving System32/PATH cannot silently satisfy its imports.
    if (LoadLibraryExW(L"vc-dynamic-control.dll", nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)) return 3;
    if (argc == 2 && !extract_action(argv[1])) return 7;
    const wchar_t* libraries[] = {L"seiza_cabi.dll", L"seiza_platform.dll", L"SeizaThumbnailProvider.dll", L"SeizaEmbeddedAction.dll"};
    const char* exports[] = {"seiza_core_version", "seiza_win_string_free", "NotifyShellAssociations", "NotifyShellAssociations"};
    for (int i = 0; i < (argc == 2 ? 4 : 3); ++i) {
        auto name = libraries[i];
        HMODULE module = LoadLibraryExW(name, nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!module) { std::fwprintf(stderr, L"Could not load %ls: %lu\n", name, GetLastError()); return 4; }
        if (!GetProcAddress(module, exports[i])) return 8;
        if (i >= 2) {
            auto notify = reinterpret_cast<UINT (WINAPI*)(UINT)>(GetProcAddress(module, "NotifyShellAssociations"));
            if (!notify || notify(0) != 0) return 5;
        }
        for (auto runtime : runtimes) if (GetModuleHandleW(runtime)) return 6;
        FreeLibrary(module);
    }
    std::puts("Loaded all three native DLLs and called the MSI Shell action with VC runtime resolution blocked.");
    if (argc == 2) std::puts("The custom action extracted from the actual MSI also loaded and executed without VC redist.");
    return 0;
}
