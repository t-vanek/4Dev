// Resource-only definitions for the client without MFC; keep legacy MSBuild resources supported.
#pragma once
#ifdef TCLIENT_NO_MFC
#include <winres.h>
#define AFX_IDS_APP_TITLE 0xE000
#else
#include <afxres.h>
#endif
