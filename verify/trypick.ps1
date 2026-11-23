param([string]$id, [string]$source)
python -I D:\Source\recompbox-catalog\verify\merge.py
$env:RECOMPBOX_PICK=$id; $env:RECOMPBOX_PICK_SOURCE=$source; $env:RECOMPBOX_PICK_CATALOG='D:\AI\recompbox-picktest-catalog.json'; $env:RECOMPBOX_PICK_DIR="D:\AI\picktest\$id"
Set-Location D:\Source\LaunchBox-Windows-AI5\Tools\SM64EnhancedWindowsBuilder
dotnet test tests\SM64EnhancedWindowsBuilder.Tests -c Release --no-build --filter InstallsPickFromGameCopyWhenAsked 2>&1 | Select-String "Passed!|Failed|Error Message|Exception:" | Select-Object -First 4 | ForEach-Object { $_.Line.Trim() }
$log = Get-Content "D:\AI\picktest\$id\recompbox-test-log.txt" -ErrorAction SilentlyContinue
if ($log) { Select-String -Path $log -Pattern "Setting up|Answered|made|Installed" | ForEach-Object { $_.Line } }
