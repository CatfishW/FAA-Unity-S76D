#!/usr/bin/env python3
"""Materialize the SHA-pinned full XR-3 source profile into a NEW directory.

Project and overlay inputs come exclusively from committed Git blobs. Git LFS
objects are read from git-lfs's configured LocalMediaDir and verified in full.
This tool never checks out, stages, fetches, edits, or builds the source checkout.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
from urllib.request import Request, urlopen

SCOPES = ('Assets', 'Packages', 'ProjectSettings')
MANIFEST_PATH = 'SourceProfiles~/XR3Integration/project-overlay/profile-manifest.json'
MANIFEST_SHA256 = '8709c44678916bf4e757a5f92d449c551ad201231556809ba160077a7d2d9f95'
CHUNK = 1024 * 1024


class ProfileError(RuntimeError):
    pass


def digest_file(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(CHUNK), b''):
            h.update(block)
    return h.hexdigest()


def safe_path(value):
    if not isinstance(value, str) or not value or '\\' in value or '\0' in value:
        raise ProfileError('Invalid relative profile path')
    parts = value.split('/')
    if value.startswith('/') or any(p in ('', '.', '..') or ':' in p for p in parts):
        raise ProfileError('Unsafe relative profile path: '+value)
    return PurePosixPath(value)


def excluded(value):
    p = safe_path(value)
    return (value.startswith('Assets/Temp/') or value in ('Assets/Temp', 'Assets/Temp.meta', 'Assets/XR/Temp', 'Assets/XR/Temp.meta')
        or '__pycache__' in p.parts or p.name in ('.DS_Store', '__pycache__.meta')
        or p.suffix == '.pyc')


def validate_paths(files, directories):
    seen = {}
    for name in [*files, *directories]:
        path = safe_path(name)
        if path.parts[0] not in SCOPES or excluded(name):
            raise ProfileError('Out-of-scope or excluded profile path: '+name)
        folded = name.casefold()
        if folded in seen and seen[folded] != name:
            raise ProfileError('Case-insensitive path collision: '+name)
        seen[folded] = name
    file_set = {name.casefold() for name in files}
    if file_set & {name.casefold() for name in directories}:
        raise ProfileError('File/directory path collision')
    for name in files:
        if any(str(parent).casefold() in file_set for parent in safe_path(name).parents if str(parent) != '.'):
            raise ProfileError('File is another path parent: '+name)


def validate_manifest(manifest):
    if manifest.get('schema') != 1 or set(manifest['baselineTrees']) != set(SCOPES):
        raise ProfileError('Unsupported/incomplete profile manifest')
    validate_paths(manifest['files'], manifest['directories'])
    if set(manifest['removals']) & set(manifest['files']):
        raise ProfileError('Removal collides with final inventory')
    validate_paths(manifest['removals'], [])
    def check_hash_spec(spec):
        if (type(spec.get('size')) is not int or spec['size'] < 0
                or not re.fullmatch('[0-9a-f]{64}', spec.get('sha256', ''))):
            raise ProfileError('Invalid file hash/size specification')
    for spec in manifest['files'].values():
        check_hash_spec(spec)
        if type(spec.get('executable')) is not bool:
            raise ProfileError('Invalid executable metadata')
    for spec in manifest['removals'].values():
        check_hash_spec(spec)
    for name, change in manifest['overlay'].items():
        if name not in manifest['files']:
            raise ProfileError('Overlay target absent from full inventory')
        safe_path(change['gitPath'])
        if change['gitPath'] != MANIFEST_PATH.rsplit('/', 1)[0]+'/files/'+name:
            raise ProfileError('Unexpected overlay Git path')
        if change['previous'] is not None:
            check_hash_spec(change['previous'])
    dep = manifest['dependencies']['cesium']
    check_hash_spec(dep)
    if (dep['targetPrefix'] != 'Packages/com.cesium.unity'
            or dep['url'] != 'https://github.com/CesiumGS/cesium-unity/releases/download/v1.25.1/com.cesium.unity-1.25.1.tgz'
            or type(dep.get('unpackedSize')) is not int or dep['unpackedSize'] < 0):
        raise ProfileError('Unexpected external dependency destination/URL/size')


def lfs_pointer(data):
    if not data.startswith(b'version https://git-lfs.github.com/spec/v1\n'):
        return None
    match = re.fullmatch(rb'version https://git-lfs.github.com/spec/v1\noid sha256:([0-9a-f]{64})\nsize ([0-9]+)\n?', data)
    if match is None:
        raise ProfileError('Malformed Git LFS pointer')
    return match.group(1).decode(), int(match.group(2))


def parse_lfs_media_dir(text):
    values = [line.partition('=')[2] for line in text.splitlines() if line.startswith('LocalMediaDir=')]
    if len(values) != 1 or not values[0] or not Path(values[0]).is_absolute():
        raise ProfileError('git lfs env did not return one absolute LocalMediaDir')
    return Path(values[0])


class GitSource:
    def __init__(self, repo, revision='HEAD'):
        self.repo = Path(repo).resolve()
        self.revision = self.git('rev-parse', '--verify', revision+'^{commit}').decode().strip()
        self.batch = None
        self.media_dir = None
        self.lfs_reads = 0

    def git(self, *args):
        result = subprocess.run(['git', '-C', str(self.repo), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=dict(os.environ, GIT_OPTIONAL_LOCKS='0'))
        if result.returncode:
            raise ProfileError('Git read failed: '+result.stderr.decode(errors='replace').strip())
        return result.stdout

    def __enter__(self):
        self.batch = subprocess.Popen(['git', '-C', str(self.repo), 'cat-file', '--batch'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return self

    def __exit__(self, *args):
        if self.batch:
            self.batch.stdin.close()
            self.batch.stdout.close()
            self.batch.stderr.close()
            self.batch.wait(timeout=10)

    def raw(self, oid):
        self.batch.stdin.write((oid+'\n').encode())
        self.batch.stdin.flush()
        header = self.batch.stdout.readline().split()
        if len(header) != 3 or header[1] != b'blob':
            raise ProfileError('Required committed blob unavailable: '+oid)
        size = int(header[2])
        data = self.batch.stdout.read(size)
        if len(data) != size or self.batch.stdout.read(1) != b'\n':
            raise ProfileError('Incomplete Git blob')
        return data

    def entry(self, relative):
        safe_path(relative)
        raw = self.git('ls-tree', '-z', self.revision, '--', relative)
        rows = raw.rstrip(b'\0').split(b'\0') if raw else []
        if len(rows) != 1:
            raise ProfileError('Required profile input is not committed: '+relative)
        info, path = rows[0].split(b'\t', 1)
        mode, kind, oid = info.decode().split()
        if path.decode() != relative or kind != 'blob' or mode not in ('100644', '100755'):
            raise ProfileError('Symlink/non-file committed input refused: '+relative)
        return oid

    def tree(self, name):
        return self.git('rev-parse', self.revision+':'+name).decode().strip()

    def project_entries(self):
        result = []
        for row in self.git('ls-tree', '-rz', '--full-tree', self.revision, '--', *SCOPES).split(b'\0'):
            if not row:
                continue
            info, path = row.split(b'\t', 1)
            name = path.decode()
            safe_path(name)
            if excluded(name):
                continue
            mode, kind, oid = info.decode().split()
            if kind != 'blob' or mode not in ('100644', '100755'):
                raise ProfileError('Symlink/submodule input refused: '+name)
            result.append((name, oid, mode == '100755'))
        return result

    def resolved(self, oid):
        data = self.raw(oid)
        pointer = lfs_pointer(data)
        if pointer is None:
            return data
        digest, size = pointer
        if self.media_dir is None:
            self.media_dir = parse_lfs_media_dir(self.git('lfs', 'env').decode())
        path = self.media_dir / digest[:2] / digest[2:4] / digest
        if (any(p.is_symlink() for p in (self.media_dir, path.parent.parent, path.parent, path))
                or not path.is_file() or path.stat().st_size != size or digest_file(path) != digest):
            raise ProfileError('Git LFS object missing/corrupt: '+digest+'; fetch this revision from the S76D remote first')
        self.lfs_reads += 1
        return path.read_bytes()

    def committed(self, relative):
        return self.resolved(self.entry(relative))


def check_spec(path, spec):
    if path.is_symlink() or not path.is_file() or path.stat().st_size != spec['size'] or digest_file(path) != spec['sha256']:
        raise ProfileError('Source/result hash mismatch: '+str(path.name))
    if os.name != 'nt' and 'executable' in spec and bool(path.stat().st_mode & stat.S_IXUSR) != spec['executable']:
        raise ProfileError('Source/result executable metadata mismatch: '+str(path.name))


def write_data(path, data, executable=False):
    if path.is_symlink():
        raise ProfileError('Symlink destination refused')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    path.chmod(0o755 if executable else 0o644)


def load_manifest(source):
    data = source.committed(MANIFEST_PATH)
    if hashlib.sha256(data).hexdigest() != MANIFEST_SHA256:
        raise ProfileError('Committed profile manifest differs from the tool pin')
    manifest = json.loads(data)
    validate_manifest(manifest)
    for name in SCOPES:
        if source.tree(name) != manifest['baselineTrees'][name]:
            raise ProfileError('Committed '+name+' tree differs from the reviewed baseline; working-tree overrides are never used')
    return manifest


def acquire_archive(staging, dependency, archive=None, download=False):
    if archive is not None:
        path = Path(archive)
        if path.is_symlink():
            raise ProfileError('Archive symlink refused')
    elif download:
        path = staging/'.cesium-download.tgz'
        request = Request(dependency['url'], headers={'User-Agent':'FAA-XR3-source-profile'})
        written = 0
        with urlopen(request, timeout=30) as response, path.open('xb') as output:
            for block in iter(lambda: response.read(CHUNK), b''):
                written += len(block)
                if written > dependency['size']:
                    raise ProfileError('Cesium download exceeds pinned size')
                output.write(block)
    else:
        raise ProfileError('Supply --cesium-archive or explicitly select --download-cesium')
    check_spec(path, dependency)
    return path


def extract_cesium(archive, staging, dependency, files):
    prefix = dependency['targetPrefix']
    expected = {name[len(prefix)+1:] for name in files if name.startswith(prefix+'/')}
    seen, total = set(), 0
    with tarfile.open(archive, 'r|gz') as stream:
        for member in stream:
            if member.name in ('package', 'package/') and member.isdir():
                continue
            if not member.name.startswith('package/'):
                raise ProfileError('Unexpected archive root')
            relative = member.name[len('package/'):].rstrip('/')
            if not relative and member.isdir():
                continue
            safe_path(relative)
            if member.isdir():
                continue
            if not member.isfile() or relative not in expected or relative in seen:
                raise ProfileError('Unexpected/duplicate/link archive entry: '+relative)
            seen.add(relative)
            total += member.size
            if total > dependency['unpackedSize']:
                raise ProfileError('Archive exceeds pinned unpacked size')
            target = staging/prefix/relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with stream.extractfile(member) as source, target.open('xb') as output:
                shutil.copyfileobj(source, output, CHUNK)
            target.chmod(0o755 if files[prefix+'/'+relative]['executable'] else 0o644)
    if seen != expected or total != dependency['unpackedSize']:
        raise ProfileError('Cesium archive inventory differs from pin')


def atomic_publish(staging, output):
    """Exclusive directory rename: never replace even an empty concurrent target."""
    if os.path.lexists(output):
        raise ProfileError('Output already exists')
    if os.name == 'nt':
        os.rename(staging, output)  # Windows refuses an existing destination.
        return
    libc = ctypes.CDLL(None, use_errno=True)
    if sys.platform == 'darwin':
        rename = libc.renamex_np
        rename.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
        result = rename(os.fsencode(staging), os.fsencode(output), 4)  # RENAME_EXCL
    elif sys.platform.startswith('linux'):
        rename = getattr(libc, 'renameat2', None)
        if rename is None:
            raise ProfileError('Exclusive renameat2 unavailable; refusing non-atomic fallback')
        rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        result = rename(-100, os.fsencode(staging), -100, os.fsencode(output), 1)  # RENAME_NOREPLACE
    else:
        raise ProfileError('Exclusive publication is unsupported on this platform')
    if result:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error), str(output))


def verify_result(staging, manifest):
    actual = set()
    for scope in SCOPES:
        for path in (staging/scope).rglob('*'):
            if path.is_symlink():
                raise ProfileError('Result contains a symlink')
            if path.is_file():
                name = path.relative_to(staging).as_posix()
                actual.add(name)
                if name not in manifest['files']:
                    raise ProfileError('Unexpected result file: '+name)
                check_spec(path, manifest['files'][name])
    if actual != set(manifest['files']):
        raise ProfileError('Full source inventory is incomplete')


def prepare(source, manifest, output, archive=None, download=False):
    output = Path(output).absolute()
    if os.path.lexists(output):
        raise ProfileError('Output must be a new, absent directory')
    parent = output.parent.resolve(strict=True)
    output = parent/output.name
    if output == source.repo or source.repo in output.parents:
        raise ProfileError('Output must be outside the source checkout')
    validate_manifest(manifest)
    dependency = manifest['dependencies']['cesium']
    needed = sum(spec['size'] for spec in manifest['files'].values())
    if download:
        needed += dependency['size']
    if shutil.disk_usage(parent).free < needed+128*1024*1024:
        raise ProfileError('Insufficient free space for the complete fresh project')
    staging = Path(tempfile.mkdtemp(prefix='.faa-xr3-profile-', dir=parent))
    try:
        archive_path = acquire_archive(staging, dependency, archive, download)
        entries = source.project_entries()
        validate_paths([name for name,_,_ in entries], [])
        for name, oid, executable in entries:
            write_data(staging/name, source.resolved(oid), executable)
        for name, previous in manifest['removals'].items():
            safe_path(name)
            check_spec(staging/name, previous)
            (staging/name).unlink()
        extract_cesium(archive_path, staging, dependency, manifest['files'])
        for name, change in manifest['overlay'].items():
            target = staging/name
            if change['previous'] is None:
                if os.path.lexists(target):
                    raise ProfileError('New overlay target collides: '+name)
            else:
                check_spec(target, change['previous'])
            data = source.committed(change['gitPath'])
            spec = manifest['files'][name]
            if len(data) != spec['size'] or hashlib.sha256(data).hexdigest() != spec['sha256']:
                raise ProfileError('Committed overlay hash mismatch: '+name)
            write_data(target, data, spec['executable'])
        expected_dirs = set(manifest['directories'])
        for scope in SCOPES:
            for path in sorted((staging/scope).rglob('*'), key=lambda p:len(p.parts), reverse=True):
                if path.is_dir() and path.relative_to(staging).as_posix() not in expected_dirs:
                    path.rmdir()  # Only empty obsolete directories in our owned staging.
        for name in manifest['directories']:
            (staging/name).mkdir(parents=True, exist_ok=True)
        for name, spec in manifest['files'].items():
            if (staging/name).is_file() and not (staging/name).is_symlink():
                (staging/name).chmod(0o755 if spec['executable'] else 0o644)
        verify_result(staging, manifest)
        if download:
            archive_path.unlink()
        receipt = {'schema':1, 'sourceCommit':source.revision, 'baselineTrees':manifest['baselineTrees'],
            'profileManifestSha256':MANIFEST_SHA256, 'filesVerified':len(manifest['files']),
            'overlayFiles':len(manifest['overlay']), 'removedFiles':len(manifest['removals']),
            'lfsObjectsReadAndVerified':source.lfs_reads, 'cesiumArchiveSha256':dependency['sha256'],
            'downloadedCesium':bool(download), 'ignoredWorkingTreeChanges':True,
            'buildExecuted':False, 'crashFixClaim':False}
        (staging/'XR3-PROFILE-RECEIPT.json').write_text(json.dumps(receipt, indent=2, sort_keys=True)+'\n')
        atomic_publish(staging, output)
        return receipt
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--revision', default='HEAD', help='Committed revision containing this pinned profile; standard input trees must match')
    parser.add_argument('--output', type=Path, required=True, help='New absent directory outside the source checkout')
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--cesium-archive', type=Path, help='Existing official archive; opened read-only and SHA verified')
    group.add_argument('--download-cesium', action='store_true', help='Download only the exact pinned official archive into owned temporary staging')
    args = parser.parse_args(argv)
    try:
        with GitSource(args.repo, args.revision) as source:
            if source.git('status', '--porcelain=v1', '--untracked-files=normal').strip():
                print('Warning: uncommitted/modified/untracked working files are excluded; only committed Git objects are used.', file=sys.stderr)
            manifest = load_manifest(source)
            receipt = prepare(source, manifest, args.output, args.cesium_archive, args.download_cesium)
        print(json.dumps(receipt, sort_keys=True))
        return 0
    except (ProfileError, OSError, ValueError, tarfile.TarError, subprocess.SubprocessError) as error:
        print('XR3 profile preparation refused: '+str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
