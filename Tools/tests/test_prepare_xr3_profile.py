"""Safety checks use tiny temporary Git object databases, never the public index."""
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('prepare_xr3_profile', Path(__file__).resolve().parents[1]/'prepare_xr3_profile.py')
p = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(p)


def spec(data, executable=False):
    return {'size':len(data), 'sha256':hashlib.sha256(data).hexdigest(), 'executable':executable}


class Fixture:
    def __init__(self, folder):
        self.root = Path(folder)
        self.repo = self.root/'repo'
        self.repo.mkdir()
        self.git('init', '-q')
        self.lfs = b'actual committed LFS payload\n'
        pointer = ('version https://git-lfs.github.com/spec/v1\noid sha256:'+hashlib.sha256(self.lfs).hexdigest()+'\nsize '+str(len(self.lfs))+'\n').encode()
        self.blobs = {'Assets/base.txt':b'committed base\n', 'Assets/change.cs':b'old source\n',
            'Assets/remove.txt':b'remove me\n', 'Assets/data.bin':pointer,
            'Packages/manifest.json':b'{"dependencies":{}}\n', 'ProjectSettings/ProjectVersion.txt':b'fixture editor\n'}
        self.new = b'new committed source\n'
        self.added = b'new executable input\n'
        self.cesium = {'package.json':b'{"name":"com.cesium.unity","version":"1.25.1"}', 'icon.svg.meta':b'old import\n'}
        self.new_meta = b'new import\n'
        self.archive = self.root/'cesium.tgz'
        self.make_archive()
        base_tree = self.tree(self.blobs)
        trees = {name:self.git('rev-parse', base_tree+':'+name).decode().strip() for name in p.SCOPES}
        expected = {name:spec(data) for name,data in self.blobs.items() if name!='Assets/remove.txt'}
        expected['Assets/data.bin'] = spec(self.lfs)
        expected['Assets/change.cs'] = spec(self.new)
        expected['Assets/new.py'] = spec(self.added, True)
        for name,data in self.cesium.items():
            expected['Packages/com.cesium.unity/'+name] = spec(self.new_meta if name=='icon.svg.meta' else data)
        prefix = p.MANIFEST_PATH.rsplit('/',1)[0]+'/files/'
        overlay = {'Assets/change.cs':{'gitPath':prefix+'Assets/change.cs','previous':spec(self.blobs['Assets/change.cs'])},
            'Assets/new.py':{'gitPath':prefix+'Assets/new.py','previous':None},
            'Packages/com.cesium.unity/icon.svg.meta':{'gitPath':prefix+'Packages/com.cesium.unity/icon.svg.meta','previous':spec(self.cesium['icon.svg.meta'])}}
        self.manifest = {'schema':1, 'baselineCommitProvenance':'ancestor-not-required', 'baselineTrees':trees,
            'files':expected, 'directories':['Assets','Packages','Packages/com.cesium.unity','ProjectSettings'],
            'overlay':overlay, 'removals':{'Assets/remove.txt':spec(self.blobs['Assets/remove.txt'])},
            'dependencies':{'cesium':{**spec(self.archive.read_bytes()), 'targetPrefix':'Packages/com.cesium.unity',
                'url':'https://github.com/CesiumGS/cesium-unity/releases/download/v1.25.1/com.cesium.unity-1.25.1.tgz',
                'unpackedSize':sum(len(d) for d in self.cesium.values())}}}
        self.blobs.update({prefix+'Assets/change.cs':self.new, prefix+'Assets/new.py':self.added,
            prefix+'Packages/com.cesium.unity/icon.svg.meta':self.new_meta})
        self.commit()
        # Dirty and untracked working files must never become materializer inputs.
        (self.repo/'Assets').mkdir()
        (self.repo/'Assets/base.txt').write_bytes(b'DIRTY WORKING CONTENT\n')
        (self.repo/'Assets/untracked-private.txt').write_bytes(b'untracked private fixture\n')
        self.media = self.repo/'.git/lfs/objects'
        digest = hashlib.sha256(self.lfs).hexdigest()
        self.object = self.media/digest[:2]/digest[2:4]/digest
        self.object.parent.mkdir(parents=True)
        self.object.write_bytes(self.lfs)

    def git(self, *args, data=None):
        env = dict(os.environ, GIT_AUTHOR_NAME='Fixture', GIT_AUTHOR_EMAIL='fixture@example.invalid',
            GIT_COMMITTER_NAME='Fixture', GIT_COMMITTER_EMAIL='fixture@example.invalid')
        return subprocess.run(['git','-C',str(self.repo),*args], input=data, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, env=env, check=True).stdout

    def tree(self, blobs):
        nested = {}
        for name,data in blobs.items():
            node = nested
            parts = name.split('/')
            for part in parts[:-1]:
                node = node.setdefault(part,{})
            node[parts[-1]] = data
        def write(node):
            rows = []
            for name,value in sorted(node.items()):
                if isinstance(value,dict):
                    oid = write(value)
                    kind = '040000 tree'
                else:
                    link = isinstance(value, tuple)
                    oid = self.git('hash-object','-w','--stdin',data=value[1] if link else value).decode().strip()
                    kind = '120000 blob' if link else '100644 blob'
                rows.append((kind+' '+oid+'\t'+name).encode()+b'\0')
            return self.git('mktree','-z',data=b''.join(rows)).decode().strip()
        return write(nested)

    def commit(self):
        raw = (json.dumps(self.manifest,sort_keys=True)+'\n').encode()
        self.manifest_hash = hashlib.sha256(raw).hexdigest()
        self.blobs[p.MANIFEST_PATH] = raw
        tree = self.tree(self.blobs)
        try:
            parent = self.git('rev-parse','--verify','HEAD').decode().strip()
        except subprocess.CalledProcessError:
            parent = None
        args = ['commit-tree',tree]+(['-p',parent] if parent else [])
        self.revision = self.git(*args,data=b'synthetic source fixture\n').decode().strip()
        self.git('update-ref','HEAD',self.revision)

    def make_archive(self, extra=None):
        with tarfile.open(self.archive,'w:gz') as tar:
            for name,data in self.cesium.items():
                member = tarfile.TarInfo('package/'+name)
                member.size = len(data)
                tar.addfile(member,io.BytesIO(data))
            if extra:
                tar.addfile(extra)

    @contextlib.contextmanager
    def source(self):
        with p.GitSource(self.repo) as source:
            source.media_dir = self.media
            yield source


class MaterializerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.fixture = Fixture(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def run_prepare(self, manifest=None, output=None):
        f = self.fixture
        with f.source() as source:
            return p.prepare(source, manifest or f.manifest, output or f.root/'output', f.archive)

    def assert_no_output_or_staging(self):
        self.assertFalse((self.fixture.root/'output').exists())
        self.assertFalse(list(self.fixture.root.glob('.faa-xr3-profile-*')))

    def test_success_uses_committed_data_lfs_removals_archive_and_overlay(self):
        f = self.fixture
        before = (f.repo/'Assets/base.txt').read_bytes()
        receipt = self.run_prepare()
        out = f.root/'output'
        self.assertEqual((out/'Assets/base.txt').read_bytes(), f.blobs['Assets/base.txt'])
        self.assertEqual((out/'Assets/data.bin').read_bytes(), f.lfs)
        self.assertEqual((out/'Assets/change.cs').read_bytes(), f.new)
        self.assertEqual((out/'Packages/com.cesium.unity/icon.svg.meta').read_bytes(), f.new_meta)
        self.assertFalse((out/'Assets/remove.txt').exists())
        self.assertFalse((out/'Assets/untracked-private.txt').exists())
        self.assertEqual((f.repo/'Assets/base.txt').read_bytes(),before)
        self.assertTrue((out/'Assets/new.py').stat().st_mode&0o100)
        self.assertEqual(receipt['lfsObjectsReadAndVerified'],1)
        self.assertFalse(receipt['buildExecuted'])

    def test_manifest_pin_and_three_trees_are_checked_from_git(self):
        f = self.fixture
        with f.source() as source, patch.object(p,'MANIFEST_SHA256',f.manifest_hash):
            self.assertEqual(p.load_manifest(source)['files'], f.manifest['files'])
        with f.source() as source, patch.object(p,'MANIFEST_SHA256','0'*64), self.assertRaisesRegex(p.ProfileError,'manifest differs'):
            p.load_manifest(source)
        f.manifest['baselineTrees']['Assets'] = '0'*40
        f.commit()
        with f.source() as source, patch.object(p,'MANIFEST_SHA256',f.manifest_hash), self.assertRaisesRegex(p.ProfileError,'Assets tree'):
            p.load_manifest(source)
        self.assert_no_output_or_staging()

    def test_missing_or_same_size_corrupt_lfs_refuses_and_cleans_only_owned_staging(self):
        f = self.fixture
        for state in ('missing','corrupt'):
            with self.subTest(state=state):
                if f.object.exists():f.object.unlink()
                if state=='corrupt':f.object.write_bytes(b'x'*len(f.lfs))
                with self.assertRaisesRegex(p.ProfileError,'LFS object missing/corrupt'):
                    self.run_prepare()
                self.assert_no_output_or_staging()

    def test_interrupt_during_copy_cleans_staging_without_creating_output(self):
        with patch.object(p,'write_data',side_effect=KeyboardInterrupt('synthetic cancellation')):
            with self.assertRaises(KeyboardInterrupt):self.run_prepare()
        self.assert_no_output_or_staging()
        self.assertEqual((self.fixture.repo/'Assets/base.txt').read_bytes(),b'DIRTY WORKING CONTENT\n')

    def test_archive_and_lfs_object_symlinks_are_refused(self):
        f = self.fixture
        link = f.root/'archive-link.tgz'
        link.symlink_to(f.archive)
        with f.source() as source, self.assertRaisesRegex(p.ProfileError,'Archive symlink'):
            p.prepare(source,f.manifest,f.root/'output',link)
        self.assert_no_output_or_staging()
        original = f.object.read_bytes()
        separate = f.root/'separate-lfs-object'
        separate.write_bytes(original)
        f.object.unlink()
        f.object.symlink_to(separate)
        with self.assertRaisesRegex(p.ProfileError,'LFS object missing/corrupt'):self.run_prepare()
        self.assert_no_output_or_staging()
        self.assertEqual(separate.read_bytes(),original)

    def test_git_lfs_env_media_directory_parser_handles_spaces_and_rejects_ambiguity(self):
        self.assertEqual(p.parse_lfs_media_dir('Other=x\nLocalMediaDir=/tmp/with spaces/objects\n'),Path('/tmp/with spaces/objects'))
        for text in ('LocalMediaDir=relative','LocalMediaDir=/tmp/a\nLocalMediaDir=/tmp/b','Other=x'):
            with self.assertRaises(p.ProfileError):p.parse_lfs_media_dir(text)

    def test_existing_output_dangling_symlink_and_in_place_output_are_refused(self):
        f = self.fixture
        output = f.root/'output'
        output.mkdir()
        (output/'keep').write_text('user file')
        with self.assertRaises(p.ProfileError):self.run_prepare()
        self.assertEqual((output/'keep').read_text(),'user file')
        shutil.rmtree(output)
        output.symlink_to(f.root/'absent',target_is_directory=True)
        with self.assertRaises(p.ProfileError):self.run_prepare()
        output.unlink()
        with self.assertRaisesRegex(p.ProfileError,'outside the source'):self.run_prepare(output=f.repo/'nested-output')

    def test_late_output_collision_is_not_replaced_even_when_empty(self):
        f = self.fixture
        publish = p.atomic_publish
        def collision(staging, output):
            output.mkdir()
            return publish(staging,output)
        with patch.object(p,'atomic_publish',collision), self.assertRaises(p.ProfileError):self.run_prepare()
        self.assertTrue((f.root/'output').is_dir())
        self.assertFalse(list((f.root/'output').iterdir()))
        self.assertFalse(list(f.root.glob('.faa-xr3-profile-*')))

    def test_manifest_traversal_case_and_file_parent_collisions_refuse(self):
        for files,dirs in [({'Assets/../escape':{}},[]),({'Assets/A':{},'Assets/a':{}},[]),
                ({'Assets/a':{},'Assets/a/child':{}},[]),({'Assets/a':{}},['Assets/a']),
                ({'Assets/cache.pyc':{}},[]),({'Assets/__pycache__.meta':{}},[])]:
            with self.subTest(files=files),self.assertRaises(p.ProfileError):p.validate_paths(files,dirs)

    def test_corrupt_archive_is_refused_before_extraction(self):
        self.fixture.archive.write_bytes(b'not the pinned archive')
        with self.assertRaisesRegex(p.ProfileError,'hash mismatch'):self.run_prepare()
        self.assert_no_output_or_staging()

    def test_archive_traversal_symlink_hardlink_and_duplicate_are_refused(self):
        f = self.fixture
        for kind in ('traversal','symlink','hardlink','duplicate'):
            with self.subTest(kind=kind):
                member = tarfile.TarInfo('package/../escape' if kind=='traversal' else 'package/package.json')
                if kind in ('symlink','hardlink'):
                    member.type = tarfile.SYMTYPE if kind=='symlink' else tarfile.LNKTYPE
                    member.linkname = '../../outside'
                f.make_archive(member)
                manifest = copy.deepcopy(f.manifest)
                manifest['dependencies']['cesium'].update(spec(f.archive.read_bytes()))
                with self.assertRaises(p.ProfileError):self.run_prepare(manifest)
                self.assert_no_output_or_staging()
                self.assertFalse((f.root/'escape').exists())

    def test_committed_overlay_tamper_or_incomplete_expected_inventory_refuses(self):
        f = self.fixture
        manifest = copy.deepcopy(f.manifest)
        manifest['files']['Assets/change.cs']['sha256'] = '0'*64
        with self.assertRaisesRegex(p.ProfileError,'overlay hash mismatch'):self.run_prepare(manifest)
        self.assert_no_output_or_staging()
        manifest = copy.deepcopy(f.manifest)
        manifest['files']['Assets/missing.txt'] = spec(b'not present')
        with self.assertRaisesRegex(p.ProfileError,'inventory is incomplete'):self.run_prepare(manifest)
        self.assert_no_output_or_staging()

    def test_download_is_explicit_exact_url_pinned_and_not_kept_in_result(self):
        f = self.fixture
        data = f.archive.read_bytes()
        with f.source() as source, patch.object(p,'urlopen',return_value=io.BytesIO(data)) as request:
            receipt = p.prepare(source,f.manifest,f.root/'output',download=True)
        self.assertTrue(receipt['downloadedCesium'])
        self.assertEqual(request.call_args.args[0].full_url,f.manifest['dependencies']['cesium']['url'])
        self.assertFalse((f.root/'output/.cesium-download.tgz').exists())

    def test_cli_warns_about_dirty_working_tree_but_uses_committed_manifest(self):
        f = self.fixture
        with patch.object(p,'MANIFEST_SHA256',f.manifest_hash), contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()) as err:
            code = p.main(['--repo',str(f.repo),'--output',str(f.root/'output'),'--cesium-archive',str(f.archive)])
        self.assertEqual(code,0,err.getvalue())
        self.assertIn('uncommitted/modified/untracked',err.getvalue())
        self.assertEqual(json.loads(out.getvalue())['sourceCommit'],f.revision)

    def test_depth_one_clone_needs_matching_root_trees_not_baseline_history(self):
        f = self.fixture
        old = f.revision
        f.commit()
        clone = f.root/'shallow'
        subprocess.run(['git','clone','--quiet','--depth','1','--no-checkout',f.repo.as_uri(),str(clone)],check=True)
        absent = subprocess.run(['git','-C',str(clone),'cat-file','-e',old+'^{commit}'],capture_output=True)
        self.assertNotEqual(absent.returncode,0)
        with p.GitSource(clone) as source, patch.object(p,'MANIFEST_SHA256',f.manifest_hash):
            source.media_dir = f.media
            manifest = p.load_manifest(source)
            receipt = p.prepare(source,manifest,f.root/'output',f.archive)
        self.assertEqual(receipt['sourceCommit'],f.revision)

    def test_symlink_in_committed_project_tree_is_refused(self):
        f = self.fixture
        f.blobs['Assets/shortcut'] = ('symlink',b'../../outside')
        tree = f.tree(f.blobs)
        f.manifest['baselineTrees']['Assets'] = f.git('rev-parse',tree+':Assets').decode().strip()
        f.commit()
        with self.assertRaisesRegex(p.ProfileError,'Symlink/submodule'):
            self.run_prepare()
        self.assert_no_output_or_staging()

    def test_exclusive_native_rename_refuses_collision_after_presence_check(self):
        f = self.fixture
        staging, output = f.root/'staging', f.root/'output'
        staging.mkdir()
        (staging/'payload').write_text('owned staging')
        output.mkdir()
        inode = output.stat().st_ino
        with patch.object(p.os.path,'lexists',return_value=False), self.assertRaises(OSError):
            p.atomic_publish(staging,output)
        self.assertEqual(output.stat().st_ino,inode)
        self.assertFalse(list(output.iterdir()))
        self.assertTrue((staging/'payload').is_file())


class FullProfileBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.repo = Path(__file__).resolve().parents[2]
        cls.path = cls.repo/p.MANIFEST_PATH
        cls.raw = cls.path.read_bytes()
        cls.manifest = json.loads(cls.raw)

    def test_complete_manifest_pin_counts_and_cache_exclusions(self):
        m = self.manifest
        self.assertEqual(hashlib.sha256(self.raw).hexdigest(),p.MANIFEST_SHA256)
        p.validate_manifest(m)
        self.assertEqual(len(m['files']),10336)
        self.assertEqual(len(m['overlay']),769)
        self.assertEqual(len(m['removals']),145)
        self.assertEqual(m['counts']['baselineFiles']-145+m['counts']['newFiles']+2007,len(m['files']))
        self.assertEqual(m['countAdjustment']['orphanCacheMetaFilesExcludedFromWholeProject'],17)
        self.assertFalse(any(p.excluded(name)for name in [*m['files'],*m['directories']]))

    def test_every_overlay_payload_matches_exact_frozen_file_hash_and_guid(self):
        for name,change in self.manifest['overlay'].items():
            with self.subTest(name=name):
                source = self.repo/change['gitPath']
                self.assertFalse(source.is_symlink())
                self.assertEqual(source.stat().st_size,self.manifest['files'][name]['size'])
                self.assertEqual(p.digest_file(source),self.manifest['files'][name]['sha256'])
        self.assertEqual(sum(v['kind']=='cesium-import-metadata'for v in self.manifest['overlay'].values()),10)

    def test_standard_committed_root_trees_and_existing_flattened_snapshots_unchanged(self):
        for name,expected in self.manifest['baselineTrees'].items():
            actual = subprocess.check_output(['git','-C',str(self.repo),'rev-parse','HEAD:'+name],text=True).strip()
            self.assertEqual(actual,expected)
        folder = self.repo/'SourceProfiles~/XR3Integration'
        files = list(folder.glob('*.cs'))
        self.assertEqual(len(files),46)
        for path in files:
            relative = path.relative_to(self.repo).as_posix()
            original = subprocess.check_output(['git','-C',str(self.repo),'show','HEAD:'+relative])
            self.assertEqual(path.read_bytes(),original,relative)
            meta = Path(str(path)+'.meta')
            if meta.is_file():
                relative = meta.relative_to(self.repo).as_posix()
                original = subprocess.check_output(['git','-C',str(self.repo),'show','HEAD:'+relative])
                self.assertEqual(meta.read_bytes(),original,relative)

    def test_actual_release_builder_uses_experiment_scene_and_keeps_qualification_explicit(self):
        m = self.manifest
        key = 'Assets/_Project/Scripts/Headset/Editor/XR3ReleaseBuild.cs'
        source = (self.repo/m['overlay'][key]['gitPath']).read_text()
        self.assertIn('MainScene = "Assets/_Project/Scenes/ExperimentScene.unity"',source)
        self.assertEqual(m['sourceIdentity']['buildScene'],'Assets/_Project/Scenes/ExperimentScene.unity')
        self.assertFalse(m['sourceIdentity']['nativeCrashResolved'])
        self.assertEqual(m['scan']['nonemptyCredentialValuesFound'],0)


if __name__ == '__main__':
    unittest.main()
