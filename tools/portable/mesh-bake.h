// Explorer-owned x64 engine for dense tiles that exceed wasm32's address space.
// Uploads and results are temporary files; up to eight independent native bakes run at a time.
static LONG nativeBakeActive;
#define NATIVE_BAKE_MAX 8

static int reserveNativeBake(void) {
    LONG active=InterlockedCompareExchange(&nativeBakeActive,0,0);
    while(active<NATIVE_BAKE_MAX) {
        LONG seen=InterlockedCompareExchange(&nativeBakeActive,active+1,active);
        if(seen==active)return 1;
        active=seen;
    }
    return 0;
}

static void nativeBakerPath(wchar_t *path, size_t size) {
	GetModuleFileNameW(NULL, path, (DWORD)size);
	wchar_t *slash = wcsrchr(path, L'\\');
	if (slash) slash[1] = 0;
	wcsncat(path, L"mesh-engine\\Explorer.Mesh.exe", size - wcslen(path) - 1);
}

static int nativeSend(SOCKET socket,const char* data,int length) {
    while(length>0){int n=send(socket,data,length,0);if(n<=0)return 0;data+=n;length-=n;}return 1;
}
static int nativeFrame(SOCKET socket,unsigned char kind,const char* body,unsigned int length) {
    unsigned char header[5]={kind,(unsigned char)length,(unsigned char)(length>>8),(unsigned char)(length>>16),(unsigned char)(length>>24)};
    return nativeSend(socket,(const char*)header,5)&&(!length||nativeSend(socket,body,(int)length));
}
static int nativeResult(SOCKET socket,const wchar_t* path) {
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);LARGE_INTEGER size;
    if(file==INVALID_HANDLE_VALUE)return nativeFrame(socket,2,"Native result file is missing",29);
    if(!GetFileSizeEx(file,&size)||size.QuadPart<0||size.QuadPart>1073741824LL){CloseHandle(file);return nativeFrame(socket,2,"Native result size is invalid",29);}
    unsigned int length=(unsigned int)size.QuadPart;
    unsigned char header[5]={1,(unsigned char)length,(unsigned char)(length>>8),(unsigned char)(length>>16),(unsigned char)(length>>24)};
    int okay=nativeSend(socket,(const char*)header,5);char buffer[65536];DWORD count;unsigned int remaining=length;
    while(okay&&remaining){if(!ReadFile(file,buffer,remaining<sizeof buffer?remaining:sizeof buffer,&count,NULL)||!count){okay=0;break;}okay=nativeSend(socket,buffer,(int)count);remaining-=count;}
    CloseHandle(file);return okay;
}

static void nativeBakeBusy(SOCKET socket,long long remaining,const char* reason) {
    char discard[65536];
    while(remaining>0){
        int n=recv(socket,discard,(int)(remaining<(long long)sizeof discard?remaining:(long long)sizeof discard),0);
        if(n<=0)return;
        remaining-=n;
    }
    sendStatus(socket,"POST","429 Too Many Requests",reason);
}

static void nativeBake(SOCKET socket, const char *request, const char *initial, int received) {
	char origin[256] = {0}, expected[256], custom[32], sizeText[64], transfer[64];
	snprintf(expected, sizeof expected, "http://127.0.0.1:%d", serverPort);
	int validOrigin = headerValue(request, "Origin", origin, sizeof origin) && strcmp(origin, expected) == 0;
	snprintf(expected, sizeof expected, "http://localhost:%d", serverPort);
	validOrigin = validOrigin || strcmp(origin, expected) == 0;
	if (!addressedHere(request) || !validOrigin || !headerValue(request, "X-Explorer-Mesh", custom, sizeof custom) || strcmp(custom, "1") != 0) {
		sendStatus(socket, "POST", "403 Forbidden", "Native baking requires an Explorer-origin request."); return;
	}
	char *end;
	if (!headerValue(request, "Content-Length", sizeText, sizeof sizeText) || headerValue(request, "Transfer-Encoding", transfer, sizeof transfer)) {
		sendStatus(socket, "POST", "411 Length Required", "A fixed-length bake input is required."); return;
	}
	long long total = strtoll(sizeText, &end, 10);
	if (*end || total < 4 || total > 1073741824LL || received > total) { sendStatus(socket, "POST", "413 Content Too Large", "Invalid bake input length."); return; }
	MEMORYSTATUSEX memory={0};memory.dwLength=sizeof memory;
    if(GlobalMemoryStatusEx(&memory) && memory.ullAvailPhys < 2ULL*1024*1024*1024) { nativeBakeBusy(socket,total-received,"Waiting for at least 2 GiB of available system memory.");return; }
    if (!reserveNativeBake()) { nativeBakeBusy(socket,total-received,"All eight native bake slots are busy.");return; }
	wchar_t engine[MAX_PATH], temp[MAX_PATH], input[MAX_PATH] = L"", output[MAX_PATH + 8] = L"", log[MAX_PATH + 8] = L"";
	HANDLE upload = INVALID_HANDLE_VALUE, logfile = INVALID_HANDLE_VALUE, nullin = INVALID_HANDLE_VALUE, job = NULL;
	PROCESS_INFORMATION process = {0};
    int streaming=0;
	nativeBakerPath(engine, MAX_PATH);
	if (GetFileAttributesW(engine) == INVALID_FILE_ATTRIBUTES) { sendStatus(socket, "POST", "503 Service Unavailable", "Explorer's native mesh engine is missing from this portable package."); goto cleanup; }
	if (!GetTempPathW(MAX_PATH, temp) || !GetTempFileNameW(temp, L"mex", 0, input)) { sendStatus(socket, "POST", "500 Internal Server Error", "Cannot create native bake scratch file."); goto cleanup; }
	swprintf(output, MAX_PATH + 8, L"%ls.zip", input); swprintf(log, MAX_PATH + 8, L"%ls.log", input);
	upload = CreateFileW(input, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, NULL);
	if (upload == INVALID_HANDLE_VALUE) { sendStatus(socket, "POST", "500 Internal Server Error", "Cannot write native bake input."); goto cleanup; }
	char buffer[65536]; DWORD written;
	if (received && (!WriteFile(upload, initial, (DWORD)received, &written, NULL) || written != (DWORD)received)) goto uploadFailed;
	while (received < total) {
		int wanted = (int)((total - received) < sizeof buffer ? total - received : sizeof buffer);
		int count = recv(socket, buffer, wanted, 0);
		if (count <= 0 || !WriteFile(upload, buffer, (DWORD)count, &written, NULL) || written != (DWORD)count) goto uploadFailed;
		received += count;
	}
	CloseHandle(upload); upload = INVALID_HANDLE_VALUE;
	SECURITY_ATTRIBUTES security = { sizeof security, NULL, TRUE };
	logfile = CreateFileW(log, GENERIC_WRITE, FILE_SHARE_READ, &security, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, NULL);
	nullin = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, NULL);
	if (logfile == INVALID_HANDLE_VALUE || nullin == INVALID_HANDLE_VALUE) { sendStatus(socket, "POST", "500 Internal Server Error", "Cannot open native engine diagnostics."); goto cleanup; }
	STARTUPINFOEXW startup = {0}; startup.StartupInfo.cb = sizeof startup;
	startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
	startup.StartupInfo.hStdInput = nullin; startup.StartupInfo.hStdOutput = logfile; startup.StartupInfo.hStdError = logfile;
	SIZE_T attributeBytes = 0; InitializeProcThreadAttributeList(NULL, 1, 0, &attributeBytes);
	startup.lpAttributeList = HeapAlloc(GetProcessHeap(), 0, attributeBytes);
	HANDLE inherited[] = { logfile, nullin };
	int attributesReady = startup.lpAttributeList && InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &attributeBytes);
	int handlesReady = attributesReady && UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof inherited, NULL, NULL);
	wchar_t command[MAX_PATH * 4];
	char lowMemory[8]={0};
    int recovery=headerValue(request,"X-Explorer-Low-Memory",lowMemory,sizeof lowMemory)&&strcmp(lowMemory,"1")==0;
    swprintf(command,MAX_PATH*4,L"\"%ls\" \"%ls\" \"%ls\"%ls",engine,input,output,recovery?L" --low-memory":L"");
	int started = handlesReady && CreateProcessW(engine, command, NULL, NULL, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT, NULL, NULL, &startup.StartupInfo, &process);
	if (attributesReady) DeleteProcThreadAttributeList(startup.lpAttributeList);
	if (startup.lpAttributeList) HeapFree(GetProcessHeap(), 0, startup.lpAttributeList);
	if (!started) { sendStatus(socket, "POST", "500 Internal Server Error", "Could not start Explorer's x64 mesh engine."); goto cleanup; }
	job = CreateJobObjectW(NULL, NULL);
	JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {0}; limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
	if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof limits) || !AssignProcessToJobObject(job, process.hProcess)) {
		TerminateProcess(process.hProcess, 1); sendStatus(socket, "POST", "500 Internal Server Error", "Could not supervise native bake cancellation."); goto cleanup;
	}
    char streamHeader[8]={0};streaming=headerValue(request,"X-Explorer-Stream",streamHeader,sizeof streamHeader)&&strcmp(streamHeader,"1")==0;
    if(streaming){
        const char* response="HTTP/1.1 200 OK\r\nContent-Type: application/x-explorer-bake-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        if(!nativeSend(socket,response,(int)strlen(response))||!nativeFrame(socket,0,NULL,0))goto cleanup;
    }
    ULONGLONG heartbeat=GetTickCount64();
	ResumeThread(process.hThread);
	while (WaitForSingleObject(process.hProcess, 100) == WAIT_TIMEOUT) {
        if(streaming&&GetTickCount64()-heartbeat>=1000){if(!nativeFrame(socket,0,NULL,0))goto cleanup;heartbeat=GetTickCount64();}
		fd_set readable; FD_ZERO(&readable); FD_SET(socket, &readable); struct timeval wait = {0, 0};
		if (select(0, &readable, NULL, NULL, &wait) > 0 && recv(socket, buffer, 1, MSG_PEEK) <= 0) goto cleanup;
	}
	DWORD exitCode = 1; GetExitCodeProcess(process.hProcess, &exitCode);
	CloseHandle(logfile); logfile = INVALID_HANDLE_VALUE;
	if (exitCode == 0) {if(streaming)nativeResult(socket,output);else sendFile(socket, "GET", output, "application/zip", NULL);}
	else {
		char error[17000]; int prefix = snprintf(error, sizeof error, "Native x64 mesh bake failed (exit %lu):\n", (unsigned long)exitCode);
		HANDLE diagnostics = CreateFileW(log, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
		DWORD read = 0;
		if (diagnostics != INVALID_HANDLE_VALUE) { LARGE_INTEGER size; GetFileSizeEx(diagnostics, &size); if (size.QuadPart > 16000) { size.QuadPart -= 16000; SetFilePointerEx(diagnostics, size, NULL, FILE_BEGIN); } ReadFile(diagnostics, error + prefix, 16000, &read, NULL); CloseHandle(diagnostics); }
		error[prefix + read] = 0; if(streaming)nativeFrame(socket,2,error,prefix+read);else sendStatus(socket, "POST", "500 Internal Server Error", error);
	}
	goto cleanup;
uploadFailed:
	sendStatus(socket, "POST", "500 Internal Server Error", "Native input upload interrupted or disk full.");
cleanup:
	if (job) { TerminateJobObject(job, 1); if (process.hProcess) WaitForSingleObject(process.hProcess, INFINITE); CloseHandle(job); }
	if (process.hThread) CloseHandle(process.hThread);
	if (process.hProcess) { WaitForSingleObject(process.hProcess, INFINITE); CloseHandle(process.hProcess); }
	if (upload != INVALID_HANDLE_VALUE) CloseHandle(upload);
	if (logfile != INVALID_HANDLE_VALUE) CloseHandle(logfile);
	if (nullin != INVALID_HANDLE_VALUE) CloseHandle(nullin);
	if (input[0]) DeleteFileW(input);
	if (output[0]) DeleteFileW(output);
	if (log[0]) DeleteFileW(log);
	InterlockedDecrement(&nativeBakeActive);
}
