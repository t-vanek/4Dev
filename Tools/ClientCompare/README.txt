ClientCompare — read-only comparison of the installed 4Story client and Game/

Reports, raw extracted records, portable tools and Ghidra projects stay in the
ignored .runtime/ and build/ directories. The game EXEs are never executed.

From the repository root in PowerShell:

python -m pip install --target .runtime\client-compare-tools\pylib pefile capstone
python Tools\ClientCompare\setup_tools.py
python Tools\ClientCompare\unpack_mpress.py "C:\Program Files (x86)\4Story\TClient.exe" .runtime\client-comparison-2026-10-27
python Tools\ClientCompare\compare.py --official "C:\Program Files (x86)\4Story" --ours Game --output .runtime\client-comparison-2026-10-27
python Tools\ClientCompare\extract_missing.py --output .runtime\client-comparison-2026-10-27
.\Tools\ClientCompare\run_decompiler.ps1 -Client official
.\Tools\ClientCompare\run_decompiler.ps1 -Client ours
python Tools\ClientCompare\feature_evidence.py .runtime\client-comparison-2026-10-27
python Tools\ClientCompare\make_report.py .runtime\client-comparison-2026-10-27

Run the Ghidra imports once per project name; existing projects deliberately
require an explicit new name or manual selection, rather than silent overwrite.

The MPRESS reader recognizes the checked x86 2.1x LZMA loader layout. It writes
an analysis PE with restored imports and entry point. It is not a deployable
replacement EXE. Decompilation produces inferred C-like pseudocode, not the
original C++ sources. A feature is never considered absent solely because a
class name or string is absent. Validate server protocol and behavior separately
before porting. Raw record exports include headers and require index rebuilding
and dependency resolution before use in a game pack.
