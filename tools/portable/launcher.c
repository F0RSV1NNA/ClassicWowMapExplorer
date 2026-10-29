// Map Explorer's portable launcher: serves the built app (the "app" folder next to this exe)
// on 127.0.0.1 and shows it in a window of its own (Edge's or Chrome's app mode), with no
// console. The app needs a web server: module workers and fetch don't work from file:// pages.
// It also finds World of Warcraft and serves its files to the page, so there's no folder to
// choose. Closing the window stops it.
//
// Built by tools/buildPortable.ts (gcc from mingw-w64).

#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

// The same port every time, so the browser keeps the page's settings (they're per origin);
// the next ones along if something else has it.
#define FIRST_PORT 51730
#define PORT_TRIES 20
// Answered by a running launcher, so a second one just opens the page instead of another server.
#define HELLO_PATH "/__mapexplorer"
#define HELLO_BODY "Map Explorer native-mesh-v1"
// The World of Warcraft install, served to the page here so it opens without asking for the folder.
#define WOW_PATH "/__wow/"

static wchar_t appDir[MAX_PATH];
// The install found at startup (the folder holding .build.info); empty if there's none.
static wchar_t wowDir[MAX_PATH];
// The port being served, which requests for the game files must be addressed to.
static int serverPort;
struct MimeType {
	const char *extension;
	const char *type;
};

static const struct MimeType MIME_TYPES[] = {
	{".html", "text/html; charset=utf-8"},
	{".js", "text/javascript; charset=utf-8"},
	{".mjs", "text/javascript; charset=utf-8"},
	{".css", "text/css; charset=utf-8"},
	{".json", "application/json"},
	{".svg", "image/svg+xml"},
	{".png", "image/png"},
	{".ico", "image/x-icon"},
	{".wasm", "application/wasm"},
	{".woff2", "font/woff2"},
	{".txt", "text/plain; charset=utf-8"},
};

static const char *mimeType(const char *path) {
	const char *dot = strrchr(path, '.');
	if (dot) {
		for (size_t i = 0; i < sizeof MIME_TYPES / sizeof MIME_TYPES[0]; i++) {
			if (_stricmp(dot, MIME_TYPES[i].extension) == 0) return MIME_TYPES[i].type;
		}
	}
	return "application/octet-stream";
}

static void sendAll(SOCKET s, const char *data, int length) {
	while (length > 0) {
		int sent = send(s, data, length, 0);
		if (sent <= 0) return;
		data += sent;
		length -= sent;
	}
}

// A short text answer; its body only for GET (an answer to HEAD has none, or it would run into
// the next answer on the connection).
static void sendStatus(SOCKET s, const char *method, const char *status, const char *body) {
	char header[256];
	int n = snprintf(header, sizeof header,
		"HTTP/1.1 %s\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: %d\r\n\r\n",
		status, (int)strlen(body));
	sendAll(s, header, n);
	if (strcmp(method, "HEAD") != 0) sendAll(s, body, (int)strlen(body));
}

static int hexValue(char c) {
	if (c >= '0' && c <= '9') return c - '0';
	if (c >= 'a' && c <= 'f') return c - 'a' + 10;
	if (c >= 'A' && c <= 'F') return c - 'A' + 10;
	return -1;
}

// Decodes %XX in place and drops the query or fragment. False for a path that could leave
// the app folder or name something odd.
static int cleanPath(char *path) {
	char *end = strpbrk(path, "?#");
	if (end) *end = 0;
	char *out = path;
	for (char *in = path; *in; in++) {
		if (in[0] == '%' && hexValue(in[1]) >= 0 && hexValue(in[2]) >= 0) {
			*out++ = (char)(hexValue(in[1]) * 16 + hexValue(in[2]));
			in += 2;
		} else {
			*out++ = *in;
		}
	}
	*out = 0;
	if (path[0] != '/' || strchr(path, '\\') || strchr(path, ':') || strstr(path, "..")) return 0;
	for (char *c = path; *c; c++) {
		if ((unsigned char)*c < 32) return 0;
	}
	return 1;
}

// A request header's value, copied to out; false if the request has none.
static int headerValue(const char *request, const char *name, char *out, size_t size) {
	size_t nameLength = strlen(name);
	for (const char *line = strstr(request, "\r\n"); line && line[2]; line = strstr(line + 2, "\r\n")) {
		const char *value = line + 2;
		if (_strnicmp(value, name, nameLength) != 0 || value[nameLength] != ':') continue;
		value += nameLength + 1;
		while (*value == ' ') value++;
		size_t n = strcspn(value, "\r\n");
		if (n >= size) n = size - 1;
		memcpy(out, value, n);
		out[n] = 0;
		return 1;
	}
	return 0;
}

// Whether a request names this server as 127.0.0.1 or localhost, so no web page elsewhere can
// reach the game files through a name of its own that points here (DNS rebinding).
static int addressedHere(const char *request) {
	char host[256];
	char expected[64];
	if (!headerValue(request, "Host", host, sizeof host)) return 0;
	snprintf(expected, sizeof expected, "127.0.0.1:%d", serverPort);
	if (_stricmp(host, expected) == 0) return 1;
	snprintf(expected, sizeof expected, "localhost:%d", serverPort);
	return _stricmp(host, expected) == 0;
}

// A cleaned URL path (UTF-8, '/' separated) -> a file or folder under root, with no '\' at the end.
static void localPath(const wchar_t *root, const char *path, wchar_t *out, size_t size) {
	wchar_t relative[2048];
	MultiByteToWideChar(CP_UTF8, 0, path, -1, relative, 2048);
	for (wchar_t *c = relative; *c; c++) {
		if (*c == L'/') *c = L'\\';
	}
	swprintf(out, size, L"%ls%ls%ls", root, relative[0] == L'\\' ? L"" : L"\\", relative);
	size_t n = wcslen(out);
	while (n > 0 && out[n - 1] == L'\\') out[--n] = 0;
}

// Sends a file, or the part a Range header asks for ("bytes=first-last" or "bytes=first-").
// Opened shared for writing too, so the game files can be read while the game runs.
static void sendFile(SOCKET s, const char *method, const wchar_t *file, const char *type, const char *range) {
	DWORD attributes = GetFileAttributesW(file);
	HANDLE f = attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) ? INVALID_HANDLE_VALUE
		: CreateFileW(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	LARGE_INTEGER size;
	if (f == INVALID_HANDLE_VALUE || !GetFileSizeEx(f, &size)) {
		if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
		sendStatus(s, method, "404 Not Found", "Not found");
		return;
	}
	long long first = 0;
	long long last = size.QuadPart - 1;
	long long a = 0;
	long long b = 0;
	int parts = range ? sscanf(range, "bytes=%lld-%lld", &a, &b) : 0;
	if (parts >= 1) {
		if (a < 0 || a >= size.QuadPart || (parts == 2 && b < a)) {
			CloseHandle(f);
			char header[256];
			int n = snprintf(header, sizeof header,
				"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */%lld\r\nContent-Length: 0\r\n\r\n",
				(long long)size.QuadPart);
			sendAll(s, header, n);
			return;
		}
		first = a;
		if (parts == 2 && b < last) last = b;
	}
	char contentRange[128] = "";
	if (parts >= 1) snprintf(contentRange, sizeof contentRange, "Content-Range: bytes %lld-%lld/%lld\r\n", first, last, (long long)size.QuadPart);
	char header[512];
	int n = snprintf(header, sizeof header,
		"HTTP/1.1 %s\r\nContent-Type: %s\r\nContent-Length: %lld\r\n%sAccept-Ranges: bytes\r\nCache-Control: no-cache\r\n\r\n",
		parts >= 1 ? "206 Partial Content" : "200 OK", type, last - first + 1, contentRange);
	sendAll(s, header, n);
	if (strcmp(method, "GET") == 0) {
		LARGE_INTEGER position;
		position.QuadPart = first;
		SetFilePointerEx(f, position, NULL, FILE_BEGIN);
		long long remaining = last - first + 1;
		char chunk[65536];
		DWORD read;
		while (remaining > 0 && ReadFile(f, chunk, (DWORD)(remaining < (long long)sizeof chunk ? remaining : (long long)sizeof chunk), &read, NULL) && read > 0) {
			sendAll(s, chunk, (int)read);
			remaining -= read;
		}
	}
	CloseHandle(f);
}

// Lists a folder for the page: the names in it, one per line, in UTF-8.
static void sendListing(SOCKET s, const char *method, const wchar_t *dir) {
	wchar_t pattern[MAX_PATH * 2];
	swprintf(pattern, sizeof pattern / sizeof pattern[0], L"%ls\\*", dir);
	WIN32_FIND_DATAW found;
	HANDLE find = FindFirstFileW(pattern, &found);
	if (find == INVALID_HANDLE_VALUE) {
		sendStatus(s, method, "404 Not Found", "Not found");
		return;
	}
	size_t capacity = 4096;
	size_t length = 0;
	char *body = malloc(capacity);
	do {
		if (!body || wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0) continue;
		char name[MAX_PATH * 4];
		int n = WideCharToMultiByte(CP_UTF8, 0, found.cFileName, -1, name, sizeof name, NULL, NULL);
		if (n <= 1) continue;
		while (length + n > capacity) {
			char *bigger = realloc(body, capacity *= 2);
			if (!bigger) free(body);
			body = bigger;
			if (!body) break;
		}
		if (!body) continue;
		memcpy(body + length, name, n - 1);
		length += n - 1;
		body[length++] = '\n';
	} while (FindNextFileW(find, &found));
	FindClose(find);
	if (!body) {
		sendStatus(s, method, "500 Internal Server Error", "Out of memory");
		return;
	}
	char header[256];
	int n = snprintf(header, sizeof header,
		"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: %d\r\nCache-Control: no-cache\r\n\r\n", (int)length);
	sendAll(s, header, n);
	if (strcmp(method, "GET") == 0) sendAll(s, body, (int)length);
	free(body);
}

#include "mesh-bake.h"

// Answers one request; false when the connection should close after it.
static int answer(SOCKET s, const char *request) {
	char method[8] = {0};
	char path[2048] = {0};
	if (sscanf(request, "%7s %2047s", method, path) != 2) {
		sendStatus(s, "GET", "400 Bad Request", "Bad request");
		return 0;
	}
	if (strcmp(method, "GET") != 0 && strcmp(method, "HEAD") != 0) {
		sendStatus(s, "GET", "405 Method Not Allowed", "Only GET and HEAD");
		return 0;
	}
	char connection[32];
	int keepOpen = !(headerValue(request, "Connection", connection, sizeof connection) && _stricmp(connection, "close") == 0);
	wchar_t file[MAX_PATH * 2];
	if (!cleanPath(path)) {
		sendStatus(s, method, "404 Not Found", "Not found");
	} else if (strcmp(path, HELLO_PATH) == 0) {
		sendStatus(s, method, "200 OK", HELLO_BODY);
	} else if (strcmp(path, "/__mesh-native") == 0) {
		wchar_t engine[MAX_PATH]; nativeBakerPath(engine, MAX_PATH);
		sendStatus(s, method, "200 OK", addressedHere(request) && GetFileAttributesW(engine) != INVALID_FILE_ATTRIBUTES ? "{\"available\":true,\"backend\":\"native-x64\",\"version\":1}" : "{\"available\":false}");
	} else if (strncmp(path, WOW_PATH, strlen(WOW_PATH)) == 0) {
		const char *relative = path + strlen(WOW_PATH);
		// Only what the page reads: .build.info and the Data folder.
		int allowed = wowDir[0] && addressedHere(request) && (strcmp(relative, ".build.info") == 0 || _strnicmp(relative, "Data/", 5) == 0);
		char range[128];
		if (!allowed) {
			sendStatus(s, method, "404 Not Found", "Not found");
		} else {
			localPath(wowDir, relative, file, sizeof file / sizeof file[0]);
			if (relative[strlen(relative) - 1] == '/') sendListing(s, method, file);
			else sendFile(s, method, file, "application/octet-stream", headerValue(request, "Range", range, sizeof range) ? range : NULL);
		}
	} else {
		if (path[strlen(path) - 1] == '/') strncat(path, "index.html", sizeof path - strlen(path) - 1);
		localPath(appDir, path, file, sizeof file / sizeof file[0]);
		sendFile(s, method, file, mimeType(path), NULL);
	}
	return keepOpen;
}

// Answers requests on a connection until it closes. It stays open between them, as the page
// reads the game files in many small pieces; browsers don't pipeline, so each request comes
// on its own. A connection left idle closes after a while, ending its thread.
static DWORD WINAPI serve(LPVOID param) {
	SOCKET s = (SOCKET)(ULONG_PTR)param;
	DWORD idle = 30000;
	setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&idle, sizeof idle);
	char request[8192];
	for (;;) {
		// The request line and headers (there's no body for GET or HEAD).
		int length = 0;
		request[0] = 0;
		while (length < (int)sizeof request - 1 && !strstr(request, "\r\n\r\n")) {
			int n = recv(s, request + length, (int)sizeof request - 1 - length, 0);
			if (n <= 0) break;
			length += n;
			request[length] = 0;
		}
		char *headersEnd = strstr(request, "\r\n\r\n");
		if (!headersEnd) break;
		if (strncmp(request, "POST /__mesh-bake HTTP/1.1\r\n", 28) == 0) {
			const char *body = headersEnd + 4; int received = length - (int)(body - request);
			headersEnd[2] = 0;
			nativeBake(s, request, body, received); break;
		}
		if (!answer(s, request)) break;
	}
	shutdown(s, SD_SEND);
	closesocket(s);
	return 0;
}

// Makes a folder the install if it is one, or is inside one: the registry can name a game
// version's folder (_classic_ and the like), one below the install.
static int useInstall(const wchar_t *candidate) {
	wchar_t dir[MAX_PATH];
	wcsncpy(dir, candidate, MAX_PATH - 1);
	dir[MAX_PATH - 1] = 0;
	for (int up = 0; up < 3; up++) {
		size_t n = wcslen(dir);
		while (n > 0 && dir[n - 1] == L'\\') dir[--n] = 0;
		if (n == 0) return 0;
		wchar_t probe[MAX_PATH * 2];
		swprintf(probe, MAX_PATH * 2, L"%ls\\.build.info", dir);
		DWORD info = GetFileAttributesW(probe);
		swprintf(probe, MAX_PATH * 2, L"%ls\\Data\\data", dir);
		DWORD data = GetFileAttributesW(probe);
		if (info != INVALID_FILE_ATTRIBUTES && data != INVALID_FILE_ATTRIBUTES && (data & FILE_ATTRIBUTE_DIRECTORY)) {
			wcscpy(wowDir, dir);
			return 1;
		}
		wchar_t *slash = wcsrchr(dir, L'\\');
		if (!slash) return 0;
		*slash = 0;
	}
	return 0;
}

// Tries a registry value in a key and its subkeys; with onlyWow, only in the subkeys named for
// World of Warcraft (the installer adds one for each game version).
static int fromRegistry(const wchar_t *key, const wchar_t *value, int onlyWow) {
	wchar_t path[MAX_PATH];
	DWORD bytes = sizeof path;
	if (!onlyWow && RegGetValueW(HKEY_LOCAL_MACHINE, key, value, RRF_RT_REG_SZ, NULL, path, &bytes) == ERROR_SUCCESS && useInstall(path)) return 1;
	HKEY k;
	if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_READ, &k) != ERROR_SUCCESS) return 0;
	int found = 0;
	wchar_t name[256];
	for (DWORD i = 0; !found; i++) {
		DWORD length = 256;
		if (RegEnumKeyExW(k, i, name, &length, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
		if (onlyWow && wcsncmp(name, L"World of Warcraft", 17) != 0) continue;
		bytes = sizeof path;
		if (RegGetValueW(k, name, value, RRF_RT_REG_SZ, NULL, path, &bytes) == ERROR_SUCCESS) found = useInstall(path);
	}
	RegCloseKey(k);
	return found;
}

// Looks for World of Warcraft: where its installer says it is, then the usual folders on each
// hard drive. If it isn't found, the page asks for the folder instead.
static void findInstall(void) {
	if (fromRegistry(L"SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", L"InstallLocation", 1)) return;
	if (fromRegistry(L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", L"InstallLocation", 1)) return;
	if (fromRegistry(L"SOFTWARE\\WOW6432Node\\Blizzard Entertainment\\World of Warcraft", L"InstallPath", 0)) return;
	static const wchar_t *const FOLDERS[] = {L"Program Files (x86)\\World of Warcraft", L"Program Files\\World of Warcraft", L"World of Warcraft", L"Games\\World of Warcraft"};
	DWORD drives = GetLogicalDrives();
	for (int d = 2; d < 26; d++) {
		wchar_t root[4] = {(wchar_t)(L'A' + d), L':', L'\\', 0};
		if (!(drives & (1u << d)) || GetDriveTypeW(root) != DRIVE_FIXED) continue;
		for (size_t i = 0; i < sizeof FOLDERS / sizeof FOLDERS[0]; i++) {
			wchar_t path[MAX_PATH];
			swprintf(path, MAX_PATH, L"%ls%ls", root, FOLDERS[i]);
			if (useInstall(path)) return;
		}
	}
}

// Whether a Map Explorer launcher already answers on this port.
static int alreadyRunning(int port) {
	SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (s == INVALID_SOCKET) return 0;
	struct sockaddr_in address = {0};
	address.sin_family = AF_INET;
	address.sin_port = htons((u_short)port);
	address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	int found = 0;
	if (connect(s, (struct sockaddr *)&address, sizeof address) == 0) {
		const char *hello = "GET " HELLO_PATH " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
		sendAll(s, hello, (int)strlen(hello));
		char reply[1024] = {0};
		int length = 0, n;
		while (length < (int)sizeof reply - 1 && (n = recv(s, reply + length, (int)sizeof reply - 1 - length, 0)) > 0) length += n;
		found = strstr(reply, HELLO_BODY) != NULL;
	}
	closesocket(s);
	return found;
}

static void fail(const wchar_t *message) {
	MessageBoxW(NULL, message, L"Map Explorer", MB_OK | MB_ICONERROR);
}

// A browser that can show a page as an app window (--app): Edge, which every Windows 10 and 11
// has, else Chrome. Found through App Paths, as Windows' Run box does, then the usual places.
static int findBrowser(wchar_t *out, DWORD size) {
	static const wchar_t *const NAMES[] = {L"msedge.exe", L"chrome.exe"};
	static const wchar_t *const FOLDERS[] = {L"Microsoft\\Edge\\Application\\msedge.exe", L"Google\\Chrome\\Application\\chrome.exe"};
	static const wchar_t *const ROOTS[] = {L"ProgramFiles(x86)", L"ProgramFiles", L"LOCALAPPDATA"};
	for (int i = 0; i < 2; i++) {
		wchar_t key[128];
		swprintf(key, 128, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\%ls", NAMES[i]);
		HKEY hives[] = {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE};
		for (int h = 0; h < 2; h++) {
			DWORD bytes = size * sizeof(wchar_t);
			if (RegGetValueW(hives[h], key, NULL, RRF_RT_REG_SZ, NULL, out, &bytes) == ERROR_SUCCESS && GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES) return 1;
		}
		for (int r = 0; r < 3; r++) {
			wchar_t root[MAX_PATH];
			if (!GetEnvironmentVariableW(ROOTS[r], root, MAX_PATH)) continue;
			swprintf(out, size, L"%ls\\%ls", root, FOLDERS[i]);
			if (GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES) return 1;
		}
	}
	return 0;
}

// Whether a folder can be written to (a probe file that deletes itself).
static int writable(const wchar_t *dir) {
	wchar_t probe[MAX_PATH * 2];
	swprintf(probe, MAX_PATH * 2, L"%ls\\.writable", dir);
	HANDLE f = CreateFileW(probe, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, NULL);
	if (f == INVALID_HANDLE_VALUE) return 0;
	CloseHandle(f);
	return 1;
}

// Where the app window keeps its browser data (settings, the chosen folder's permission):
// data\browser next to the exe, so it travels with the portable folder and stays apart from
// your own browser profile; in %LOCALAPPDATA% if the exe's folder is read-only.
static void profileDir(const wchar_t *exeDir, wchar_t *out, size_t size) {
	swprintf(out, size, L"%lsdata", exeDir);
	CreateDirectoryW(out, NULL);
	wcsncat(out, L"\\browser", size - wcslen(out) - 1);
	CreateDirectoryW(out, NULL);
	if (writable(out)) return;
	wchar_t local[MAX_PATH];
	GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
	swprintf(out, size, L"%ls\\MapExplorer", local);
	CreateDirectoryW(out, NULL);
	wcsncat(out, L"\\browser", size - wcslen(out) - 1);
	CreateDirectoryW(out, NULL);
}

// Whether a browser still has the profile open: it holds its "lockfile" while it runs.
static int profileInUse(const wchar_t *profile) {
	wchar_t lock[MAX_PATH * 2];
	swprintf(lock, MAX_PATH * 2, L"%ls\\lockfile", profile);
	if (GetFileAttributesW(lock) == INVALID_FILE_ATTRIBUTES) return 0;
	HANDLE f = CreateFileW(lock, GENERIC_READ, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (f == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_SHARING_VIOLATION;
	CloseHandle(f);
	return 0;
}

// Opens the page in its own app window (no tabs or address bar) and returns the browser
// process; NULL if there's no such browser, when the page opens in the default browser instead.
static HANDLE openWindow(int port, const wchar_t *profile) {
	wchar_t url[64];
	swprintf(url, 64, L"http://127.0.0.1:%d/", port);
	wchar_t browser[MAX_PATH];
	if (!findBrowser(browser, MAX_PATH)) {
		ShellExecuteW(NULL, L"open", url, NULL, NULL, SW_SHOWNORMAL);
		return NULL;
	}
	wchar_t command[MAX_PATH * 4];
	swprintf(command, sizeof command / sizeof command[0],
		L"\"%ls\" --app=%ls --user-data-dir=\"%ls\" --start-maximized --no-first-run --no-default-browser-check",
		browser, url, profile);
	STARTUPINFOW startup = {sizeof startup};
	PROCESS_INFORMATION process;
	if (!CreateProcessW(browser, command, NULL, NULL, FALSE, 0, NULL, NULL, &startup, &process)) {
		ShellExecuteW(NULL, L"open", url, NULL, NULL, SW_SHOWNORMAL);
		return NULL;
	}
	CloseHandle(process.hThread);
	return process.hProcess;
}

static DWORD WINAPI acceptLoop(LPVOID param) {
	SOCKET listener = (SOCKET)(ULONG_PTR)param;
	for (;;) {
		SOCKET client = accept(listener, NULL, NULL);
		if (client == INVALID_SOCKET) continue;
		HANDLE thread = CreateThread(NULL, 0, serve, (LPVOID)(ULONG_PTR)client, 0, NULL);
		if (thread) CloseHandle(thread);
		else closesocket(client);
	}
	return 0;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show) {
	(void)instance;
	(void)previous;
	(void)show;
	// --no-browser: serve only, for testing (stop it from Task Manager).
	int noBrowser = wcsstr(arguments, L"--no-browser") != NULL;
	// Developer serving without game discovery or browser startup; no installed data touched.
	int appOnly = wcsstr(arguments, L"--app-only") != NULL;
	if (appOnly) noBrowser = 1;

	// The app folder sits next to the exe.
	wchar_t exeDir[MAX_PATH];
	GetModuleFileNameW(NULL, exeDir, MAX_PATH);
	wchar_t *slash = wcsrchr(exeDir, L'\\');
	if (slash) slash[1] = 0;
	swprintf(appDir, MAX_PATH, L"%lsapp", exeDir);
	wchar_t index[MAX_PATH * 2];
	swprintf(index, sizeof index / sizeof index[0], L"%ls\\index.html", appDir);
	if (GetFileAttributesW(index) == INVALID_FILE_ATTRIBUTES) {
		fail(L"The \"app\" folder is missing: keep MapExplorer.exe together with it.");
		return 1;
	}
	wchar_t profile[MAX_PATH * 2];
	profileDir(exeDir, profile, sizeof profile / sizeof profile[0]);

	WSADATA wsa;
	if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 1;

	SOCKET listener = INVALID_SOCKET;
	int port = 0;
	int firstPort = appOnly ? 51980 : FIRST_PORT;
	for (int p = firstPort; p < firstPort + PORT_TRIES; p++) {
		if (alreadyRunning(p)) {
			// Already running: just another window onto it.
			if (!noBrowser) {
				HANDLE window = openWindow(p, profile);
				if (window) CloseHandle(window);
			}
			WSACleanup();
			return 0;
		}
		SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
		// Only this computer can reach it.
		struct sockaddr_in address = {0};
		address.sin_family = AF_INET;
		address.sin_port = htons((u_short)p);
		address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
		BOOL exclusive = TRUE;
		setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&exclusive, sizeof exclusive);
		if (bind(s, (struct sockaddr *)&address, sizeof address) == 0 && listen(s, SOMAXCONN) == 0) {
			listener = s;
			port = p;
			break;
		}
		closesocket(s);
	}
	if (listener == INVALID_SOCKET) {
		wchar_t message[128];
		swprintf(message, 128, L"No free port between %d and %d.", FIRST_PORT, FIRST_PORT + PORT_TRIES - 1);
		fail(message);
		return 1;
	}
	serverPort = port;
	if (!appOnly) findInstall();
	HANDLE server = CreateThread(NULL, 0, acceptLoop, (LPVOID)(ULONG_PTR)listener, 0, NULL);
	if (noBrowser) {
		WaitForSingleObject(server, INFINITE);
		return 0;
	}

	HANDLE window = openWindow(port, profile);
	if (!window) {
		// No app window to watch: the page is in the default browser instead.
		MessageBoxW(NULL, L"Map Explorer is open in your browser.\n\nPress OK when you're done, to stop it.", L"Map Explorer", MB_OK | MB_ICONINFORMATION);
		return 0;
	}
	// Serve until the window closes: its browser process ends, and no other window (a second
	// launch, or one the browser handed over to) still has the profile open.
	WaitForSingleObject(window, INFINITE);
	CloseHandle(window);
	do Sleep(1000);
	while (profileInUse(profile));
	return 0;
}
