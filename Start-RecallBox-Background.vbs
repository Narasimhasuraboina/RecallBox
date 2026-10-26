Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
script = root & "\Start-RecallBox-Background.ps1"
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & script & """"
shell.Run command, 0, False
