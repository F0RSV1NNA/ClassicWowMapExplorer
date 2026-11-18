#include <winhttp.h>

static int cdnHostAllowed(const char *host) {
    size_t n=strlen(host);
    return strcmp(host,"blzddist1-a.akamaihd.net")==0 || strcmp(host,"blizzard.com")==0 || strcmp(host,"battle.net")==0 ||
        (n>13 && strcmp(host+n-13,".blizzard.com")==0) || (n>11 && strcmp(host+n-11,".battle.net")==0);
}

// Fixed HTTPS CDN hosts only; no local file paths, cookies or arbitrary URL forwarding.
static void serveCdn(SOCKET s,const char *method,const char *path,const char *request) {
    char host[256],resource[1800];const char *slash=strchr(path,'/');
    if(!slash || (size_t)(slash-path)>=sizeof host){sendStatus(s,method,"400 Bad Request","Invalid CDN resource");return;}
    memcpy(host,path,slash-path);host[slash-path]=0;
    if(!cdnHostAllowed(host)||strlen(slash)>=sizeof resource){sendStatus(s,method,"400 Bad Request","Invalid CDN host");return;}
    strcpy(resource,slash);
    for(const char *p=resource;*p;p++)if(!((*p>='a'&&*p<='z')||(*p>='A'&&*p<='Z')||(*p>='0'&&*p<='9')||strchr("/_-.",*p))){sendStatus(s,method,"400 Bad Request","Invalid CDN path");return;}
    wchar_t whost[256],wresource[1800],wmethod[8];
    MultiByteToWideChar(CP_UTF8,0,host,-1,whost,256);MultiByteToWideChar(CP_UTF8,0,resource,-1,wresource,1800);MultiByteToWideChar(CP_UTF8,0,method,-1,wmethod,8);
    HINTERNET session=WinHttpOpen(L"MapExplorer/0.4",WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,WINHTTP_NO_PROXY_NAME,WINHTTP_NO_PROXY_BYPASS,0),connection=NULL,remote=NULL;
    int sent=0;
    if(!session)goto done;
    WinHttpSetTimeouts(session,15000,15000,45000,45000);
    connection=WinHttpConnect(session,whost,INTERNET_DEFAULT_HTTPS_PORT,0);if(!connection)goto done;
    remote=WinHttpOpenRequest(connection,wmethod,wresource,NULL,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,WINHTTP_FLAG_SECURE);if(!remote)goto done;
    DWORD redirects=WINHTTP_OPTION_REDIRECT_POLICY_NEVER;WinHttpSetOption(remote,WINHTTP_OPTION_REDIRECT_POLICY,&redirects,sizeof redirects);
    char range[128];if(headerValue(request,"Range",range,sizeof range)){
        unsigned long long begin,end;char extra;
        if(sscanf(range,"bytes=%llu-%llu%c",&begin,&end,&extra)!=2||end<begin){sendStatus(s,method,"400 Bad Request","Invalid CDN range");sent=1;goto done;}
        wchar_t header[180];swprintf(header,180,L"Range: bytes=%llu-%llu\r\n",begin,end);WinHttpAddRequestHeaders(remote,header,(DWORD)-1,WINHTTP_ADDREQ_FLAG_ADD);
    }
    if(!WinHttpSendRequest(remote,WINHTTP_NO_ADDITIONAL_HEADERS,0,WINHTTP_NO_REQUEST_DATA,0,0,0)||!WinHttpReceiveResponse(remote,NULL))goto done;
    DWORD status=0,size=sizeof status;if(!WinHttpQueryHeaders(remote,WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,WINHTTP_HEADER_NAME_BY_INDEX,&status,&size,WINHTTP_NO_HEADER_INDEX))goto done;
    char headers[1024];int at=snprintf(headers,sizeof headers,"HTTP/1.1 %lu CDN response\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n",status);
    wchar_t value[256];size=sizeof value;
    if(WinHttpQueryHeaders(remote,WINHTTP_QUERY_CONTENT_LENGTH,WINHTTP_HEADER_NAME_BY_INDEX,value,&size,WINHTTP_NO_HEADER_INDEX)){char text[256];WideCharToMultiByte(CP_UTF8,0,value,-1,text,sizeof text,NULL,NULL);at+=snprintf(headers+at,sizeof headers-at,"Content-Length: %s\r\n",text);}
    size=sizeof value;
    if(WinHttpQueryHeaders(remote,WINHTTP_QUERY_CONTENT_RANGE,WINHTTP_HEADER_NAME_BY_INDEX,value,&size,WINHTTP_NO_HEADER_INDEX)){char text[256];WideCharToMultiByte(CP_UTF8,0,value,-1,text,sizeof text,NULL,NULL);at+=snprintf(headers+at,sizeof headers-at,"Content-Range: %s\r\n",text);}
    at+=snprintf(headers+at,sizeof headers-at,"\r\n");sendAll(s,headers,at);sent=1;
    if(strcmp(method,"HEAD")!=0){char buffer[65536];DWORD got;while(WinHttpReadData(remote,buffer,sizeof buffer,&got)&&got)sendAll(s,buffer,(int)got);}
 done:
    if(!sent)sendStatus(s,method,"502 Bad Gateway","CDN request failed");
    if(remote)WinHttpCloseHandle(remote);
    if(connection)WinHttpCloseHandle(connection);
    if(session)WinHttpCloseHandle(session);
}
