$ErrorActionPreference='Continue'
$dir=Join-Path $env:LOCALAPPDATA 'FAA-XR3\XPlane3080'
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$env:PATH="$env:WINDIR\System32\OpenSSH;"+$env:PATH
$ssh=Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
while($true){
 foreach($name in @('connection.log','ssh-error.log')){
  $file=Join-Path $dir $name
  if((Test-Path $file) -and (Get-Item $file).Length -gt 1048576){Move-Item $file ($file+'.previous') -Force}
 }
 ('Starting X-Plane 3080 SSH connection at '+(Get-Date).ToUniversalTime().ToString('o')) | Add-Content (Join-Path $dir 'connection.log')
 & $ssh -N -T -o BatchMode=yes -o ExitOnForwardFailure=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -L 127.0.0.1:8086:127.0.0.1:8086 -L 127.0.0.1:8767:127.0.0.1:8767 -L 127.0.0.1:12679:127.0.0.1:8767 3080 1>> (Join-Path $dir 'connection.log') 2>> (Join-Path $dir 'ssh-error.log')
 ('SSH exited '+$LASTEXITCODE+' at '+(Get-Date).ToUniversalTime().ToString('o')+'; retrying in 10 seconds.') | Add-Content (Join-Path $dir 'connection.log')
 Start-Sleep -Seconds 10
}
