# Windows Debugging Tools Setup

## Overview
Bluetask uses the Windows Console Debugger (cdb.exe) to analyze crash dumps and provide detailed stability information. To bundle these tools with your application, you need to obtain them from the Windows SDK.

## Required Files

Place the following files in the appropriate architecture folder:

### For x64 (DebugTools/x64/):
```
cdb.exe               - Console Debugger
dbgcore.dll           - Debugging core library
dbgeng.dll            - Debugging engine
dbghelp.dll           - Symbol handler
dbgmodel.dll          - Debugger data model
symsrv.dll            - Symbol server client
symsrv.yes            - Symbol server configuration (create empty file)
```

### For ARM64 (DebugTools/arm64/): (Optional)
Same files as x64 but ARM64 architecture versions.

## How to Obtain

### Option 1: Install Windows SDK (Recommended)
1. Download Windows SDK from: https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/
2. Run installer and select only "Debugging Tools for Windows"
3. Find files in: `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\`
4. Copy the files listed above to `DebugTools/x64/` in your project

### Option 2: Standalone Debuggers
1. Download Windows Debugging Tools standalone: https://aka.ms/windbg/download
2. Extract and copy required files to `DebugTools/x64/`

## Folder Structure
```
Bluetask/
├── DebugTools/
│   ├── x64/
│   │   ├── cdb.exe
│   │   ├── dbgcore.dll
│   │   ├── dbgeng.dll
│   │   ├── dbghelp.dll
│   │   ├── dbgmodel.dll
│   │   ├── symsrv.dll
│   │   └── symsrv.yes
│   └── arm64/ (optional)
│       └── [same files for ARM64]
└── Program/ (publish output)
```

## Important Notes

### Licensing
- Windows Debugging Tools are part of the Windows SDK
- They are redistributable with your application
- See Windows SDK license terms: https://developer.microsoft.com/en-us/windows/downloads/sdk-terms/

### File Size
- The minimal x64 debugger package is approximately 5-8 MB
- This is a reasonable addition for the crash analysis features provided

### Testing
After copying files:
1. Build/publish your project
2. Verify files are copied to output (Program/DebugTools/x64/)
3. Run the app and check Settings > Debug for WinDbg status
4. Test crash dump analysis on the Stability Center page

## Fallback Behavior
If debugging tools are not bundled, the app will automatically check for system-installed Windows SDK in:
- `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe`
- `C:\Program Files (x86)\Windows Kits\8.1\Debuggers\x64\cdb.exe`
- PATH environment variable

However, for the best user experience, always bundle the tools with your application.


