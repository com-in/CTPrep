#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

// Native WinPE host. No managed runtime, console window or installation required.
static HWND window, parentConsole, logView, backButton, logButton;
static HANDLE worker;
static BOOL finished, failed, showLog;
static int stage, percent;
static DWORD workerCode;
static wchar_t message[512] = L"";
static wchar_t work[MAX_PATH], stagePath[MAX_PATH], logPath[MAX_PATH], statePath[MAX_PATH];
static HFONT titleFont, textFont, smallFont;
static wchar_t uiFace[LF_FACESIZE] = L"Segoe UI";

// ---------------------------------------------------------------------------
// Localization. The PE console runs codepage 437, so deploy.cmd can only pass
// ASCII stage ids; the translation lives here and follows the language the
// user picked in the main program (written to lang.txt next to this file).
// ---------------------------------------------------------------------------
typedef enum {
    STR_PREPARING,
    STR_DONE,
    STR_FAILED,
    STR_DONE_DETAIL,
    STR_FAILED_DETAIL,
    STR_RUNNING_DETAIL,
    STR_BACK,
    STR_VIEW_LOG,
    STR_WINDOW_TITLE,
    STR_MININT_ONLY,
    STR_STAGE_CHECK_ENV,
    STR_STAGE_STAGING_LETTER,
    STR_STAGE_PARTITIONING,
    STR_STAGE_APPLYING_IMAGE,
    STR_STAGE_ANSWER_FILES,
    STR_STAGE_DRIVERS,
    STR_STAGE_BOOT_CONFIG,
    STR_STAGE_CLEAN_BOOT_ENTRY,
    STR_STAGE_FINALIZING,
    STR_STAGE_DONE,
    STR_STAGE_FAILED,
    STR_COUNT
} StrId;

static BOOL english;

static const wchar_t *const kZh[STR_COUNT] = {
    L"正在准备安装...",
    L"安装完成",
    L"部署意外中止（退出码 %lu）",
    L"安装完成。",
    L"安装已中止（退出码 %lu）。之前的系统可能已经无法启动。\n日志：X:\\ctprep\\deploy.log",
    L"%d%%   |   请保持通电，不要关机。",
    L"返回维护菜单",
    L"查看部署日志",
    L"CTPrep 安装中",
    L"此部署界面只能在 WinPE 环境中运行。",
    L"正在检查部署环境",
    L"正在定位暂存分区",
    L"正在分区并格式化目标磁盘",
    L"正在应用 Windows 映像",
    L"正在写入无人值守应答文件",
    L"正在复制驱动包",
    L"正在重建引导配置",
    L"正在清理临时引导项",
    L"正在收尾",
    L"安装完成",
    L"部署失败：%s",
};

static const wchar_t *const kEn[STR_COUNT] = {
    L"Preparing installation...",
    L"Installation completed",
    L"Deployment stopped unexpectedly (exit %lu)",
    L"Installation completed.",
    L"Installation stopped (exit %lu). The previous system may no longer boot.\nLogs: X:\\ctprep\\deploy.log",
    L"%d%%   |   Please keep the computer powered on.",
    L"Return to maintenance",
    L"View deployment log",
    L"CTPrep installation",
    L"This deployment host runs only inside WinPE.",
    L"Checking the deployment environment",
    L"Resolving the staging volume",
    L"Partitioning and formatting the target disk",
    L"Applying the Windows image",
    L"Writing unattended setup files",
    L"Copying driver packages",
    L"Rebuilding the boot configuration",
    L"Cleaning up the temporary boot entry",
    L"Finalizing installation",
    L"Installation completed",
    L"Deployment failed: %s",
};

static const wchar_t *S(StrId id) { return english ? kEn[id] : kZh[id]; }

// Reads lang.txt (ASCII) written by the main program. Anything starting with
// "en" selects English; everything else falls back to Simplified Chinese.
static void detectLanguage(void) {
    wchar_t path[MAX_PATH];
    char buffer[64];
    DWORD count = 0;
    HANDLE file;

    swprintf(path, MAX_PATH, L"%ls\\lang.txt", work);
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    ReadFile(file, buffer, sizeof(buffer) - 1, &count, NULL);
    CloseHandle(file);
    buffer[count] = 0;

    for (DWORD i = 0; i < count; ++i) {
        char c = buffer[i];
        if (c == ' ' || c == '\r' || c == '\n' || c == '\t') continue;
        english = (c == 'e' || c == 'E');
        return;
    }
}

static DWORD readTail(const wchar_t *path, char *buffer, DWORD capacity) {
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER size, offset;
    GetFileSizeEx(f, &size);
    offset.QuadPart = size.QuadPart > capacity - 1 ? size.QuadPart - (capacity - 1) : 0;
    SetFilePointerEx(f, offset, NULL, FILE_BEGIN);
    DWORD count = 0;
    ReadFile(f, buffer, capacity - 1, &count, NULL);
    CloseHandle(f);
    buffer[count] = 0;
    return count;
}

// Maps the ASCII stage id emitted by deploy.cmd to a localized string.
// Returns -1 when the id is unknown (the raw label is then shown as-is).
static int stageFromId(const char *id) {
    if (!strcmp(id, "CHECK_ENV")) return STR_STAGE_CHECK_ENV;
    if (!strcmp(id, "STAGING_LETTER")) return STR_STAGE_STAGING_LETTER;
    if (!strcmp(id, "PARTITIONING")) return STR_STAGE_PARTITIONING;
    if (!strcmp(id, "APPLYING_IMAGE")) return STR_STAGE_APPLYING_IMAGE;
    if (!strcmp(id, "ANSWER_FILES")) return STR_STAGE_ANSWER_FILES;
    if (!strcmp(id, "DRIVERS")) return STR_STAGE_DRIVERS;
    if (!strcmp(id, "BOOT_CONFIG")) return STR_STAGE_BOOT_CONFIG;
    if (!strcmp(id, "CLEAN_BOOT_ENTRY")) return STR_STAGE_CLEAN_BOOT_ENTRY;
    if (!strcmp(id, "FINALIZING")) return STR_STAGE_FINALIZING;
    if (!strcmp(id, "DONE")) return STR_STAGE_DONE;
    if (!strcmp(id, "FAILED")) return STR_STAGE_FAILED;
    return -1;
}

// "ID" or "ID|detail" -> localized text in message[]
static void applyStageLabel(const char *label) {
    char id[64], detail[256];
    const char *separator = strchr(label, '|');
    size_t idLength = separator ? (size_t)(separator - label) : strlen(label);

    if (idLength >= sizeof(id)) idLength = sizeof(id) - 1;
    memcpy(id, label, idLength);
    id[idLength] = 0;

    detail[0] = 0;
    if (separator && separator[1]) {
        strncpy(detail, separator + 1, sizeof(detail) - 1);
        detail[sizeof(detail) - 1] = 0;
    }

    int text = stageFromId(id);
    if (text < 0) {
        // Unknown id: show whatever the script sent (keeps older scripts usable)
        wchar_t raw[512];
        MultiByteToWideChar(CP_UTF8, 0, label, -1, raw, 512);
        raw[511] = 0;
        wcsncpy(message, raw, 511);
        message[511] = 0;
        return;
    }

    if (text == STR_STAGE_FAILED) {
        wchar_t wide[256];
        MultiByteToWideChar(CP_UTF8, 0, detail, -1, wide, 256);
        wide[255] = 0;
        swprintf(message, 512, S(STR_STAGE_FAILED), wide);
        return;
    }

    wcsncpy(message, S((StrId)text), 511);
    message[511] = 0;
}

static void readProgress(void) {
    char state[1024], tail[32768];
    if (readTail(statePath, state, sizeof(state))) {
        int value;
        char label[512];
        if (sscanf(state, "%d|%511[^\r\n]", &value, label) == 2) {
            stage = value;
            if (value >= 0 && value <= 100 && value > percent) percent = value;
            applyStageLabel(label);
        }
    }
    if (stage == 45 && readTail(logPath, tail, sizeof(tail))) {
        // DISM /English emits [==== 12.3% ====] records, often separated by CR only.
        for (char *p = tail; *p; ++p) {
            if (*p != '%') continue;
            char *begin = p;
            while (begin > tail && ((begin[-1] >= '0' && begin[-1] <= '9') || begin[-1] == '.')) --begin;
            if (begin == p) continue;
            double apply = strtod(begin, NULL);
            int total = 45 + (int)(apply * 0.24);
            if (apply >= 0 && apply <= 100 && total > percent) percent = total;
        }
    }
}

static void fill(HDC dc, RECT rect, COLORREF color) {
    HBRUSH brush = CreateSolidBrush(color);
    FillRect(dc, &rect, brush);
    DeleteObject(brush);
}

static void text(HDC dc, HFONT font, RECT rect, const wchar_t *value, COLORREF color) {
    HGDIOBJ old = SelectObject(dc, font);
    SetTextColor(dc, color);
    SetBkMode(dc, TRANSPARENT);
    DrawTextW(dc, value, -1, &rect, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);
    SelectObject(dc, old);
}

static void paint(HWND hwnd) {
    PAINTSTRUCT ps;
    HDC target = BeginPaint(hwnd, &ps), dc = CreateCompatibleDC(target);
    RECT r; GetClientRect(hwnd, &r);
    HBITMAP bitmap = CreateCompatibleBitmap(target, r.right, r.bottom);
    HGDIOBJ old = SelectObject(dc, bitmap);
    fill(dc, r, RGB(15,23,42));
    int width = min(760, r.right - 80), left = (r.right-width)/2, top = max(40, r.bottom/2-175);
    RECT box = {left, top, left+width, top+70};
    text(dc, titleFont, box, L"CTPrep", RGB(241,245,249));
    box.top += 78; box.bottom += 105;
    text(dc, textFont, box, message, failed ? RGB(251,113,133) : RGB(226,232,240));
    RECT bar = {left, top+175, left+width, top+191};
    fill(dc, bar, RGB(51,65,85));
    bar.right = left + width*percent/100;
    fill(dc, bar, failed ? RGB(244,63,94) : RGB(56,189,248));
    wchar_t detail[512];
    if (failed) swprintf(detail, 512, S(STR_FAILED_DETAIL), workerCode);
    else if (finished) wcscpy(detail, S(STR_DONE_DETAIL));
    else swprintf(detail, 512, S(STR_RUNNING_DETAIL), percent);
    box.top = top+215; box.bottom = top+290;
    text(dc, smallFont, box, detail, RGB(148,163,184));
    BitBlt(target, 0,0,r.right,r.bottom,dc,0,0,SRCCOPY);
    SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); EndPaint(hwnd, &ps);
}

// ---------------------------------------------------------------------------
// Fonts. WinPE images normally ship without the FontLink tables that let GDI
// substitute a CJK font for a glyph the requested face is missing, so drawing
// with "Segoe UI" renders every Chinese character as a box. The UI therefore
// carries its own font and links it into this binary (font_data.c, generated by
// tools/build-pe-font.py). Registering it from memory means the deployment no
// longer depends on a font file reaching the RAM disk: an earlier attempt wrote
// the .ttf into the staging volume but startnet.cmd never copied it to
// X:\ctprep, so the UI silently fell back to a Latin face and boxed every
// Chinese character.
// ---------------------------------------------------------------------------
extern const unsigned char kCtPrepFontData[];
extern const unsigned int kCtPrepFontSize;

// Characters used to decide whether a face really draws Chinese. All of them are
// covered by the embedded subset.
static const wchar_t kFontProbe[] = L"汉字部署映像";
#define FONT_PROBE_LEN ((int)(sizeof(kFontProbe)/sizeof(kFontProbe[0]) - 1))

// Creates the requested face and reports the family GDI actually realised plus
// the glyph ids it produced. GDI silently substitutes another family when the
// requested one cannot be realised, and in an English WinPE that substitute has
// no CJK coverage - which is exactly the "every Chinese character is a box"
// screenshot this whole file exists to explain. 0xFFFF means "this face has no
// such glyph".
static BOOL probeFace(HDC dc, const wchar_t *requested, wchar_t *realized, WORD *glyphs) {
    LOGFONTW lf = {0};
    HFONT font; HGDIOBJ old; BOOL ok;

    lf.lfCharSet = DEFAULT_CHARSET;
    lf.lfPitchAndFamily = DEFAULT_PITCH;
    lstrcpynW(lf.lfFaceName, requested, LF_FACESIZE);
    font = CreateFontIndirectW(&lf);
    if (!font) return FALSE;
    old = SelectObject(dc, font);
    GetTextFaceW(dc, LF_FACESIZE, realized);
    ok = GetGlyphIndicesW(dc, kFontProbe, FONT_PROBE_LEN, glyphs,
                          GGI_MARK_NONEXISTING_GLYPHS) != GDI_ERROR;
    SelectObject(dc, old);
    DeleteObject(font);
    return ok;
}

static BOOL faceHasChinese(HDC dc, const wchar_t *face) {
    wchar_t realized[LF_FACESIZE] = L"";
    WORD glyphs[FONT_PROBE_LEN];
    int i;

    if (!probeFace(dc, face, realized, glyphs)) return FALSE;
    for (i = 0; i < FONT_PROBE_LEN; ++i) {
        if (glyphs[i] == 0xFFFF) return FALSE;
    }
    return TRUE;
}

// Records what the UI settled on. Chinese only turns into boxes when no face
// with CJK coverage could be reached, so leaving this behind in the PE turns a
// screenshot into an answer instead of another round of guessing. Written as
// UTF-8 with a BOM so it opens correctly in the main system.
static void logFontChoice(HDC dc, const wchar_t *requested, const wchar_t *source) {
    static const char bom[3] = {(char)0xEF, (char)0xBB, (char)0xBF};
    wchar_t path[MAX_PATH], line[512], hex[64], realized[LF_FACESIZE] = L"";
    WORD glyphs[FONT_PROBE_LEN] = {0};
    char utf8[1024];
    HANDLE file;
    DWORD written;
    int bytes, offset, i;

    if (!probeFace(dc, requested, realized, glyphs)) {
        wcscpy(realized, L"(glyph probe failed)");
    }

    offset = 0;
    for (i = 0; i < FONT_PROBE_LEN; ++i) {
        offset += swprintf(hex + offset, 64 - offset, L"%04X ", (unsigned)glyphs[i]);
    }
    hex[63] = 0;

    swprintf(path, MAX_PATH, L"%ls\\ui-font.log", work);
    file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL,
                       CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return;

    WriteFile(file, bom, sizeof(bom), &written, NULL);
    swprintf(line, 512,
             L"requested=%ls\r\nrealized=%ls\r\nsource=%ls\r\nprobe=%ls\r\nglyphs=%ls\r\n",
             requested, realized, source, kFontProbe, hex);
    bytes = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8) - 1, NULL, NULL);
    if (bytes > 1) WriteFile(file, utf8, (DWORD)(bytes - 1), &written, NULL);
    CloseHandle(file);
}

static void resolveUiFont(void) {
    // Families a Chinese WinPE normally ships with, most preferred first. They
    // are only a safety net: an English PE has none of them.
    static const wchar_t *const fallbacks[] = {
        L"Microsoft YaHei UI", L"Microsoft YaHei", L"DengXian", L"SimHei", L"SimSun"
    };
    wchar_t path[MAX_PATH], how[256], size[64];
    HDC dc = GetDC(NULL);
    DWORD loaded = 0;
    size_t i;

    if (!dc) return;

    // 1. The font linked into this binary. Nothing has to be copied anywhere, so
    //    a missing or misplaced file can no longer bring the boxes back.
    swprintf(size, 64, L"embedded in CTPrep.PeUi.exe, %lu bytes",
             (unsigned long)kCtPrepFontSize);
    if (AddFontMemResourceEx((void *)kCtPrepFontData, kCtPrepFontSize, NULL, &loaded)
            && loaded > 0 && faceHasChinese(dc, L"CTPrep UI")) {
        wcscpy(uiFace, L"CTPrep UI");
        logFontChoice(dc, uiFace, size);
        ReleaseDC(NULL, dc);
        return;
    }

    // 2. Optional override: a CTPrep.Font.ttf dropped next to this binary wins,
    //    so the font can be swapped without recompiling the UI.
    swprintf(path, MAX_PATH, L"%ls\\CTPrep.Font.ttf", work);
    if (AddFontResourceExW(path, FR_PRIVATE, 0) > 0 && faceHasChinese(dc, L"CTPrep UI")) {
        wcscpy(uiFace, L"CTPrep UI");
        logFontChoice(dc, uiFace, L"file next to the UI");
        ReleaseDC(NULL, dc);
        return;
    }

    // 3. Whatever CJK family this PE build happens to carry.
    for (i = 0; i < sizeof(fallbacks)/sizeof(fallbacks[0]); ++i) {
        if (faceHasChinese(dc, fallbacks[i])) {
            wcscpy(uiFace, fallbacks[i]);
            logFontChoice(dc, uiFace, L"family installed in this PE");
            ReleaseDC(NULL, dc);
            return;
        }
    }

    // 4. Nothing with CJK coverage: Chinese will be boxes. Leave the reason
    //    behind instead of having to guess it from a screenshot.
    swprintf(how, 256, L"NONE - embedded font rejected (loaded=%lu), no CJK family in this PE",
             (unsigned long)loaded);
    logFontChoice(dc, L"Segoe UI", how);
    ReleaseDC(NULL, dc);
}

static void layout(HWND hwnd) {
    int x=GetSystemMetrics(SM_XVIRTUALSCREEN), y=GetSystemMetrics(SM_YVIRTUALSCREEN);
    int w=GetSystemMetrics(SM_CXVIRTUALSCREEN), h=GetSystemMetrics(SM_CYVIRTUALSCREEN);
    SetWindowPos(hwnd, HWND_TOPMOST, x,y,w,h, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    if (backButton) MoveWindow(backButton, w/2-140, h-65, 280,38,TRUE);
    if (logView) MoveWindow(logView, 30,30,w-60,h-115,TRUE);
}

static void finish(HWND hwnd, DWORD code) {
    finished = TRUE; workerCode = code; failed = code != 0;
    readProgress();
    if (!failed) { percent=100; wcsncpy(message, S(STR_DONE), 511); message[511]=0; }
    else if (stage != -1) swprintf(message,512,S(STR_FAILED),code);
    backButton = CreateWindowW(L"BUTTON",S(STR_BACK),WS_CHILD|WS_VISIBLE|WS_TABSTOP,
                              0,0,0,0,hwnd,(HMENU)1,NULL,NULL);
    // Button captions are Chinese too, so they need our font as well.
    SendMessageW(backButton,WM_SETFONT,(WPARAM)textFont,TRUE);
    if (failed) {
        char bytes[32768]; wchar_t content[32768];
        DWORD n=readTail(logPath,bytes,sizeof(bytes));
        int chars=MultiByteToWideChar(CP_OEMCP,0,bytes,n,content,32767); content[chars]=0;
        logView=CreateWindowExW(WS_EX_CLIENTEDGE,L"EDIT",content,
            WS_CHILD|WS_VSCROLL|ES_MULTILINE|ES_READONLY|ES_AUTOVSCROLL,
            0,0,0,0,hwnd,(HMENU)2,NULL,NULL);
        logButton=CreateWindowW(L"BUTTON",S(STR_VIEW_LOG),WS_CHILD|WS_VISIBLE|WS_TABSTOP,
            24,GetSystemMetrics(SM_CYVIRTUALSCREEN)-65,210,38,hwnd,(HMENU)3,NULL,NULL);
        SendMessageW(logButton,WM_SETFONT,(WPARAM)textFont,TRUE);
        SendMessageW(logView,WM_SETFONT,(WPARAM)smallFont,TRUE);
    }
    layout(hwnd);
}

static LRESULT CALLBACK proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch(msg) {
    case WM_PAINT: paint(hwnd); return 0;
    case WM_ERASEBKGND: return 1;
    case WM_CLOSE: if(finished) DestroyWindow(hwnd); return 0;
    case WM_QUERYENDSESSION: return finished;
    case WM_SYSCOMMAND:
        if ((wp&0xFFF0)==SC_CLOSE || (wp&0xFFF0)==SC_MINIMIZE) return 0;
        break;
    case WM_DISPLAYCHANGE: layout(hwnd); return 0;
    case WM_TIMER:
        if (!finished) readProgress();
        if (!finished && worker && WaitForSingleObject(worker,0)==WAIT_OBJECT_0) {
            DWORD code=1; GetExitCodeProcess(worker,&code); CloseHandle(worker); worker=NULL;
            finish(hwnd,code);
        }
        SetWindowPos(hwnd,HWND_TOPMOST,0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE);
        InvalidateRect(hwnd,NULL,FALSE); return 0;
    case WM_COMMAND:
        if(finished && LOWORD(wp)==1) DestroyWindow(hwnd);
        if(finished && LOWORD(wp)==3 && logView) { showLog=!showLog; ShowWindow(logView,showLog?SW_SHOW:SW_HIDE); }
        return 0;
    case WM_DESTROY:
        if(parentConsole) ShowWindow(parentConsole,SW_SHOW);
        PostQuitMessage(failed?1:0); return 0;
    }
    return DefWindowProcW(hwnd,msg,wp,lp);
}

int WINAPI wWinMain(HINSTANCE instance,HINSTANCE previous,LPWSTR command,int show) {
    (void)previous;(void)command;(void)show;
    // Locate our own folder first: the language file sits next to this binary.
    GetModuleFileNameW(NULL,work,MAX_PATH);
    wchar_t *slash=wcsrchr(work,L'\\');
    if(!slash) return 2;
    *slash=0;
    detectLanguage();
    wcsncpy(message, S(STR_PREPARING), 511);
    message[511]=0;

    // Refuse to execute the deployment payload in a normal Windows session.
    HKEY key;
    if(RegOpenKeyExW(HKEY_LOCAL_MACHINE,L"SYSTEM\\CurrentControlSet\\Control\\MiniNT",0,KEY_READ,&key)!=ERROR_SUCCESS) {
        MessageBoxW(NULL,S(STR_MININT_ONLY),L"CTPrep",MB_OK|MB_ICONERROR); return 2;
    }
    RegCloseKey(key);
    int argc; LPWSTR *argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(argc!=2 || wcslen(argv[1])>=MAX_PATH || wcspbrk(argv[1],L"\"%\r\n!")) return 2;
    wcscpy(stagePath,argv[1]); LocalFree(argv);
    swprintf(logPath,MAX_PATH,L"%ls\\deploy.log",work);
    swprintf(statePath,MAX_PATH,L"%ls\\progress.txt",work); DeleteFileW(statePath);
    SetProcessDPIAware();
    resolveUiFont();
    titleFont=CreateFontW(-48,0,0,0,FW_BOLD,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,uiFace);
    textFont=CreateFontW(-24,0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,uiFace);
    smallFont=CreateFontW(-18,0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,uiFace);
    WNDCLASSW wc={0}; wc.lpfnWndProc=proc; wc.hInstance=instance; wc.lpszClassName=L"CTPrepPeUi"; wc.hCursor=LoadCursor(NULL,IDC_ARROW);
    if(!RegisterClassW(&wc)) return 2;
    window=CreateWindowExW(WS_EX_TOPMOST,wc.lpszClassName,S(STR_WINDOW_TITLE),WS_POPUP,0,0,800,600,NULL,NULL,instance,NULL);
    if(!window) return 2;
    AttachConsole(ATTACH_PARENT_PROCESS); parentConsole=GetConsoleWindow();
    if(parentConsole) ShowWindow(parentConsole,SW_HIDE);
    FreeConsole();
    layout(window); SetForegroundWindow(window); UpdateWindow(window);
    wchar_t cmd[MAX_PATH], args[2048]; GetSystemDirectoryW(cmd,MAX_PATH); wcscat(cmd,L"\\cmd.exe");
    swprintf(args,2048,L"\"%ls\" /d /s /c \"\"%ls\\deploy.cmd\" \"%ls\"\"",cmd,work,stagePath);
    STARTUPINFOW si={0}; si.cb=sizeof(si); si.dwFlags=STARTF_USESHOWWINDOW; si.wShowWindow=SW_HIDE;
    PROCESS_INFORMATION pi={0};
    if(CreateProcessW(cmd,args,NULL,NULL,FALSE,CREATE_NO_WINDOW,NULL,work,&si,&pi)) {
        worker=pi.hProcess; CloseHandle(pi.hThread);
    } else finish(window,GetLastError());
    SetTimer(window,1,500,NULL);
    MSG event; while(GetMessageW(&event,NULL,0,0)>0) { TranslateMessage(&event); DispatchMessageW(&event); }
    DeleteObject(titleFont); DeleteObject(textFont); DeleteObject(smallFont);
    return (int)event.wParam;
}
