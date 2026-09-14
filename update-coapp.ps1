$git = $env:programfiles+"\Git\cmd\git.exe";
Write-Host $git
if (!$git) {
  Write-Host "Something went wrong. Please enter the path to git.cmd or git.exe:";
  $git = Read-Host;
  Set-Variable -Name git -Value $git;
}
ls -name -Exclude *.* | foreach {Write-Host $_;cd .\$_ ; & $git pull origin master  2> $null ; cd ..}
Write-Host "Done.";
cmd /c pause | out-null