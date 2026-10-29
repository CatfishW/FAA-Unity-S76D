#!/usr/bin/env python3
"""Managed-only regression of the embedded SDK allocator. No native address is dereferenced."""
import argparse,hashlib,json,shutil,subprocess
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--dotnet',default=shutil.which('dotnet'));p.add_argument('--source');p.add_argument('--output',required=True);a=p.parse_args()
if not a.dotnet:p.error('Provide a .NET8 SDK using --dotnet')
root=Path(__file__).resolve().parents[2];source=Path(a.source)if a.source else root/'Packages/com.ultraleap.tracking/Core/Runtime/Plugins/LeapCSharp/MemoryManager.cs'
out=Path(a.output).resolve();out.mkdir(parents=True,exist_ok=True);text=source.read_text();needle='.Enqueue('
# Insert a scheduling seam immediately after the real pool-publication statement.
# The production source remains unchanged. Both old and corrected implementations use this seam.
lines=text.splitlines();matches=[i for i,line in enumerate(lines)if needle in line]
if len(matches)!=1:raise SystemExit('Expected one SDK pool publication')
lines.insert(matches[0]+1,'                PublishedForTest?.Invoke();')
text='\n'.join(lines).replace('public static bool EnablePooling = true;','public static bool EnablePooling = true; public static Action PublishedForTest;')
(out/'SdkMemoryManager.cs').write_text(text);shutil.copyfile(Path(__file__).with_name('Program.cs'),out/'Program.cs')
(out/'Tests.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>')
results={'source':str(source.resolve()),'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'managedOnly':True,'productionSourceModified':False,'runs':[]}
build=subprocess.run([a.dotnet,'build',str(out/'Tests.csproj'),'--nologo','--verbosity','quiet'],capture_output=True,text=True);(out/'build.log').write_text(build.stdout+build.stderr);build.check_returncode()
for mode in ['interleaving','stress']:
 r=subprocess.run([a.dotnet,str(out/'bin/Debug/net8.0/Tests.dll'),mode],capture_output=True,text=True,timeout=120);(out/(mode+'.log')).write_text(r.stdout+r.stderr);results['runs'].append({'mode':mode,'exitCode':r.returncode,'output':r.stdout+r.stderr});print(mode,r.returncode,r.stdout.strip())
results['passed']=all(x['exitCode']==0 for x in results['runs']);(out/'results.json').write_text(json.dumps(results,indent=2));raise SystemExit(0 if results['passed']else 1)
