"""Opt-in controller integration regressions using synthetic state only."""
import contextlib
import copy
import hashlib
import io
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import acquisition as acquire
import checkpoint_state as state
import conservative_control as c
import flight_demo as demo
import sustain
from experimental_fixtures import (FastClock,HEADING,NativeModesApi,PROP,
    acquisition_checkpoint,f32,orbit_api,orbit_checkpoint,save,save_pair)
from test_reliability import options


class ExperimentalTests(unittest.TestCase):
    def execute(self,args,api,clock):
        with contextlib.redirect_stdout(io.StringIO())as output:
            code=demo.run_demo(args,api,clock)
        return code,json.loads(output.getvalue())

    def test_acquisition_preflight_refusals_send_no_control_commands(self):
        for kind in ('checkpoint','foreign','unpaused','clock','main'):
            clock=FastClock();api=NativeModesApi(clock)
            with tempfile.TemporaryDirectory()as directory:
                source,h=save(Path(directory)/'source',acquisition_checkpoint(False,.522))
                api.values[PROP][0]=f32(.522)
                if kind=='checkpoint':h='0'*64
                elif kind=='foreign':api.foreign=True
                elif kind=='unpaused':api.values['sim/time/paused']=0
                elif kind=='clock':api.native_start=101.
                else:api.values[PROP][0]=.5221
                args=options(Path(directory)/'state',ias_altitude_assist=True,seconds=2)
                args.resume_acquisition,args.resume_sha256=source,h
                code,r=self.execute(args,api,clock)
            self.assertEqual(code,1);self.assertFalse(r['pauseRequested'])
            self.assertFalse(r['controlRequested']);self.assertFalse(r['unpauseRequested'])
            self.assertFalse(any(a[0]in('command','write','write_index','request')for a in api.actions))

    def test_fresh_acquisition_refuses_foreign_or_unpaused_before_mode_selection(self):
        for kind in ('foreign','unpaused'):
            clock=FastClock();api=NativeModesApi(clock,ready=False)
            if kind=='foreign':api.foreign=True
            else:api.values['sim/time/paused']=0
            with tempfile.TemporaryDirectory()as directory:
                code,r=self.execute(options(Path(directory)/'state',ias_altitude_assist=True),api,clock)
            self.assertEqual(code,1);self.assertFalse(r['pauseRequested'])
            self.assertFalse(any(a[0]in('command','write','write_index')for a in api.actions))

    def test_failed_mode_mutation_or_unpause_is_owned_and_paused(self):
        for failed in ('sim/autopilot/autothrottle_hard_off','sim/operation/pause_off'):
            clock=FastClock();api=NativeModesApi(clock,ready=False);command=api.command
            def broken(name,*args,**kwargs):
                command(name,*args,**kwargs)
                if name==failed:raise OSError('uncertain requested mutation')
            api.command=broken
            with tempfile.TemporaryDirectory()as directory:
                code,r=self.execute(options(Path(directory)/'state',ias_altitude_assist=True,seconds=2),api,clock)
            self.assertEqual(code,1);self.assertTrue(r['controlRequested']);self.assertTrue(r['pauseConfirmed'])
            self.assertIn(('command','sim/operation/pause_on'),api.actions)

    def test_acquisition_selects_ias_before_unpause_and_only_writes_main(self):
        clock=FastClock();api=NativeModesApi(clock,ready=False)
        api.values[demo.P+'indicated_airspeed']=88
        api.values[demo.P+'theta']=9;api.values[demo.P+'local_vy']=4.
        with tempfile.TemporaryDirectory()as directory:
            code,r=self.execute(options(Path(directory)/'state',ias_altitude_assist=True,seconds=2),api,clock)
        self.assertEqual(code,0);self.assertTrue(r['pauseConfirmed']);self.assertFalse(r['handoffReady'])
        ias=api.actions.index(('command','sim/autopilot/level_change'))
        unpause=api.actions.index(('command','sim/operation/pause_off'))
        self.assertLess(ias,unpause)
        self.assertIn(('write','sim/cockpit2/autopilot/airspeed_dial_kts_mach',100.),api.actions[ias:unpause])
        writes=[(i,a)for i,a in enumerate(api.actions)if a[0]=='write_index']
        self.assertTrue(writes);self.assertGreater(writes[0][0],unpause)
        self.assertTrue(all(a[1:3]==(PROP,0)for _,a in writes));self.assertEqual(api.values[PROP][1],.64)
        self.assertFalse(any(a[0]=='write'and('/override/'in a[1]or a[1].endswith('prop_ratio_all'))for a in api.actions))

    def test_new_nonstable_v5_resumes_acquisition_without_reseeding(self):
        clock=FastClock();api=NativeModesApi(clock,ready=False)
        with tempfile.TemporaryDirectory()as directory:
            source_dir=Path(directory)/'fresh'
            code,first=self.execute(options(source_dir,ias_altitude_assist=True,seconds=1),api,clock)
            self.assertEqual(code,0);self.assertTrue(first['privateAcquisitionV5']);self.assertFalse(first['handoffReady'])
            source=source_dir/'latest.json';h=hashlib.sha256(source.read_bytes()).hexdigest()
            api.actions.clear();api.values[demo.P+'elevation']+=1
            args=options(Path(directory)/'resume',ias_altitude_assist=True,seconds=2)
            args.resume_acquisition,args.resume_sha256=source,h
            code,continued=self.execute(args,api,clock)
        self.assertEqual(code,0)
        self.assertEqual(continued['targetAltitudeFt'],first['targetAltitudeFt'])
        self.assertEqual(continued['initialMainBaselineNotLevelTrim'],first['initialMainBaselineNotLevelTrim'])
        self.assertFalse(any(a[0]=='command'and a[1].startswith('sim/autopilot/')for a in api.actions))

    def test_partial_initial_acquisition_report_still_preserves_resumable_state(self):
        clock=FastClock();api=NativeModesApi(clock,ready=False);command=api.command
        def delayed(name,*args,**kwargs):
            command(name,*args,**kwargs)
            if name=='sim/operation/pause_off':clock.now+=1.
        api.command=delayed
        with tempfile.TemporaryDirectory()as directory:
            target=Path(directory)/'short'
            code,r=self.execute(options(target,ias_altitude_assist=True,seconds=1),api,clock)
            source=target/'latest.json';cp=acquire.load_resume_checkpoint(source,hashlib.sha256(source.read_bytes()).hexdigest(),Path(directory)/'next')
        self.assertEqual(code,0);self.assertEqual(cp['baseline'],.5);self.assertEqual(cp['lastCommand'],.5)
        self.assertFalse(r['handoffReady'])

    def test_acquisition_refusal_cannot_overwrite_source_checkpoint(self):
        clock=FastClock();api=NativeModesApi(clock)
        with tempfile.TemporaryDirectory()as directory:
            folder=Path(directory)/'source';folder.mkdir()
            source=folder/'latest.json';source.write_text(json.dumps(acquisition_checkpoint()))
            raw=source.read_bytes();args=options(folder,ias_altitude_assist=True)
            args.resume_acquisition,args.resume_sha256=source,hashlib.sha256(raw).hexdigest()
            code,r=self.execute(args,api,clock)
            self.assertEqual(source.read_bytes(),raw)
        self.assertEqual(code,1);self.assertFalse(r['pauseRequested'])
        self.assertFalse(any(a[0]in('command','write','write_index')for a in api.actions))

    def test_nonstable_acquisition_cannot_enter_sustained_or_drop_sustained_phase(self):
        with tempfile.TemporaryDirectory()as directory:
            p,h=save(Path(directory)/'source',acquisition_checkpoint(False))
            with self.assertRaises(c.ControlError):sustain.load_checkpoint(p,h,Path(directory)/'sustain')
            data,events,_,_=orbit_checkpoint();p,h,j,jh=save_pair(Path(directory)/'source',data,events)
            with self.assertRaises(c.ControlError):acquire.load_resume_checkpoint(p,h,Path(directory)/'acquire')

    def test_v4_acquisition_checkpoint_remains_compatible(self):
        data=acquisition_checkpoint(False,.522)
        data.update(privateAcquisitionV4=True,privateAcquisitionV5=False)
        data.pop('finalPausedNativeReceipt')
        with tempfile.TemporaryDirectory()as directory:
            p,h=save(Path(directory)/'source',data)
            cp=acquire.load_resume_checkpoint(p,h,Path(directory)/'resume')
        ctl=acquire.restore_controller(cp,f32(.522),100,100)
        self.assertEqual(ctl.baseline,.5);self.assertEqual(ctl.last_written,.522)
        self.assertIsNone(ctl.stable_since)

    def test_sustained_orbit_waits_for_stability_and_first_step_retains_dial(self):
        clock=FastClock();api=NativeModesApi(clock)
        with tempfile.TemporaryDirectory()as directory:
            p,h=save(Path(directory)/'source',acquisition_checkpoint())
            args=options(Path(directory)/'sustain',sustain=True,orbit=True,seconds=40)
            args.resume_acquisition,args.resume_sha256=p,h
            code,r=self.execute(args,api,clock)
        self.assertEqual(code,0);self.assertTrue(r['pauseConfirmed']);self.assertTrue(r['orbitActive'])
        self.assertEqual(api.heading_writes[0],(30,344.))
        self.assertFalse(r['flightAccepted']);self.assertEqual(api.values[PROP][1],.64)

    def test_v6_original_orbit_migrates_and_v7_roundtrips_without_reseed(self):
        data,events,original,endpoint=orbit_checkpoint();clock=FastClock();api=orbit_api(clock,endpoint)
        with tempfile.TemporaryDirectory()as directory:
            p,h,j,jh=save_pair(Path(directory)/'v6',data,events)
            args=options(Path(directory)/'v7',sustain=True,orbit=True,seconds=3)
            args.resume_acquisition,args.resume_sha256=p,h
            args.resume_journal,args.resume_journal_sha256=j,jh
            code,r=self.execute(args,api,clock)
            self.assertEqual(code,0);self.assertEqual(api.heading_writes[0],(0,334.))
            self.assertEqual(r['orbitState']['lat0'],original.lat0)
            self.assertNotEqual((r['orbitState']['lat0'],r['orbitState']['lon0']),endpoint)
            source=args.state_dir/'latest.json';h=hashlib.sha256(source.read_bytes()).hexdigest()
            newclock=FastClock();newapi=orbit_api(newclock,endpoint)
            newapi.native_start=r['finalPausedNativeReceipt']['clockAfter']
            args=options(Path(directory)/'again',sustain=True,orbit=True,seconds=2)
            args.resume_acquisition,args.resume_sha256=source,h
            with patch.object(c.TangentOrbit,'__init__',side_effect=AssertionError('reseed')):
                code,again=self.execute(args,newapi,newclock)
        self.assertEqual(code,0);self.assertTrue(again['pauseConfirmed'])
        self.assertEqual(newapi.heading_writes[0],(0,r['headingLastCommand']))
        self.assertEqual(again['orbitState']['center'],list(original.center))

    def test_sustained_preflight_clock_main_dial_and_mode_refusals_are_read_only(self):
        for kind in ('clock','main','dial','mode'):
            data,events,_,endpoint=orbit_checkpoint();clock=FastClock();api=orbit_api(clock,endpoint)
            if kind=='clock':api.native_start=150.1
            elif kind=='main':api.values[PROP][0]=.500001
            elif kind=='dial':api.values[HEADING]=334.01
            else:api.values['sim/cockpit2/autopilot/speed_status']=0
            with tempfile.TemporaryDirectory()as directory:
                p,h,j,jh=save_pair(Path(directory)/'source',data,events)
                args=options(Path(directory)/'state',sustain=True,orbit=True,seconds=2)
                args.resume_acquisition,args.resume_sha256=p,h;args.resume_journal,args.resume_journal_sha256=j,jh
                code,r=self.execute(args,api,clock)
            self.assertEqual(code,1);self.assertFalse(r['unpauseRequested'])
            self.assertFalse(any(a[0]in('command','write','write_index')for a in api.actions))

    def test_main_quantization_is_narrow_and_active_orbit_cannot_be_dropped(self):
        data=acquisition_checkpoint(True,.50000002)
        data['finalPausedNativeReceipt']['mainCollective']=.5
        with tempfile.TemporaryDirectory()as directory:
            p,h=save(Path(directory)/'source',data);cp=sustain.load_checkpoint(p,h,Path(directory)/'state')
            ctl=sustain.restore(cp,data['finalPausedNativeReceipt']['mainCollective'],100,100)
            self.assertEqual(ctl.last_written,.50000002)
            data['finalPausedNativeReceipt']['mainCollective']+=1e-5;p,h=save(Path(directory)/'source',data)
            with self.assertRaises(c.ControlError):sustain.load_checkpoint(p,h,Path(directory)/'state')
        data,events,_,endpoint=orbit_checkpoint();clock=FastClock();api=orbit_api(clock,endpoint)
        with tempfile.TemporaryDirectory()as directory:
            p,h,j,jh=save_pair(Path(directory)/'source',data,events)
            args=options(Path(directory)/'state',sustain=True,orbit=False,seconds=2)
            args.resume_acquisition,args.resume_sha256=p,h;args.resume_journal,args.resume_journal_sha256=j,jh
            code,r=self.execute(args,api,clock)
        self.assertEqual(code,1);self.assertFalse(r['unpauseRequested']);self.assertFalse(api.heading_writes)

    def test_strict_pd_formula_and_control_envelope_are_preserved(self):
        cp={'targetFt':2500/c.FT_TO_M,'baseline':.5,'lastCommand':.52}
        ctl=sustain.SustainedCollective(cp)
        result=ctl.update(.52,2490/c.FT_TO_M,.2,100,2,0,.5,100)
        self.assertEqual(result,.514)
        self.assertAlmostEqual(ctl.diagnostic['rawTarget'],.5+.008-.0024)
        for index,value in [(1,2419/c.FT_TO_M),(2,3.01),(3,89.9),(4,15.1),(5,20.1)]:
            sample=[.5,2500/c.FT_TO_M,0,100,2,0,.5,100];sample[index]=value
            with self.assertRaises(c.ControlError):sustain.SustainedCollective({'targetFt':2500/c.FT_TO_M,'baseline':.5,'lastCommand':.5}).update(*sample)

    def test_full_circle_wrap_rate_and_lead_remain_bounded(self):
        true,variation=15.2,14.5
        orbit=c.TangentOrbit((47.651,-122.499),true,demo.ROUTE_CENTER,demo.ROUTE_LIMIT_METERS,demo.distance_bearing)
        previous=(true-variation)%360;commands=[]
        for i in range(1050):
            theta=i*.35;radial=math.radians(true+90-theta)
            position=orbit.geo(orbit.cx+c.RADIUS_M*math.sin(radial),orbit.cy+c.RADIUS_M*math.cos(radial))
            actual_true=(true-theta)%360;actual_mag=(actual_true-variation)%360
            desired,route=orbit.target(position,actual_true,actual_mag,demo.magnetic_heading)
            command=demo.limited_route_heading(previous,actual_mag,desired,0 if i==0 else 1)
            self.assertLessEqual(abs(demo.angle_delta(command,previous)),0 if i==0 else .50000001)
            self.assertLessEqual(abs(demo.angle_delta(command,actual_mag)),5.00000001)
            self.assertLess(abs(route['radialErrorMeters']),1e-5)
            commands.append(command);previous=command
        self.assertTrue(any(a<1 and b>359 for a,b in zip(commands,commands[1:])))

    def test_fixed_ring_and_finite_deadline_do_not_accumulate_history(self):
        with tempfile.TemporaryDirectory()as directory:
            evidence=sustain.Evidence(Path(directory)/'journal.jsonl')
            for i in range(300):evidence.sample({'nativeClockAfter':i,'values':{'synthetic':i}})
            self.assertEqual(len(evidence.rows),120);self.assertEqual(evidence.samples,300)
            evidence.close();self.assertEqual(len(evidence.path.read_text().splitlines()),300)
        class WideClock(FastClock):
            def sleep(self,seconds):self.now+=1800
        clock=WideClock();api=NativeModesApi(clock);read=api.read
        api.read=lambda name,*a,**kw:100+.0001*clock.now if name=='sim/time/total_flight_time_sec'else read(name,*a,**kw)
        with tempfile.TemporaryDirectory()as directory:
            p,h=save(Path(directory)/'source',acquisition_checkpoint())
            args=options(Path(directory)/'state',sustain=True,seconds=0)
            args.resume_acquisition,args.resume_sha256=p,h
            code,r=self.execute(args,api,clock)
        self.assertEqual(code,0);self.assertEqual(clock.now,1800);self.assertTrue(r['pauseConfirmed'])

    def test_cli_refuses_experimental_start_and_bad_flag_pairs_before_api_creation(self):
        paired=['--resume-acquisition','checkpoint.json','--resume-sha256','0'*64]
        cases=[['--ias-altitude-assist','--start'],['--sustain',*paired,'--start'],['--sustain'],
            ['--sustain',*paired,'--seconds','1801'],['--orbit'],
            ['--ias-altitude-assist',*paired,'--orbit'],['--sustain',*paired,'--resume-journal','journal.jsonl']]
        with patch.object(demo,'NativeApi',side_effect=AssertionError('API created')):
            for argv in cases:
                with contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit)as error:demo.main(argv)
                self.assertEqual(error.exception.code,2)


class LazyDependencyTests(unittest.TestCase):
    def test_legacy_fifty_checks_run_without_any_optional_controller_modules(self):
        here=Path(__file__).resolve().parent;source=here.parent
        with tempfile.TemporaryDirectory()as directory:
            destination=Path(directory)/'Tools'/'XPlane3080';tests=destination/'tests';tests.mkdir(parents=True)
            for name in ('flight_demo.py','start_s76.py','verify_flying.py'):shutil.copy2(source/name,destination/name)
            for name in ('test_reliability.py','test_collective_index.py'):shutil.copy2(here/name,tests/name)
            env=dict(os.environ,PYTHONPATH='',PYTHONDONTWRITEBYTECODE='1')
            run=subprocess.run([sys.executable,'-m','unittest','discover','-s',str(tests)],cwd=destination,env=env,capture_output=True,text=True,timeout=20)
        self.assertEqual(run.returncode,0,run.stdout+run.stderr)
        self.assertIn('Ran 50 tests',run.stderr)

    def test_missing_experimental_modules_fail_clearly_without_network_or_controls(self):
        source=Path(__file__).resolve().parent.parent
        with tempfile.TemporaryDirectory()as directory:
            shutil.copy2(source/'flight_demo.py',Path(directory)/'flight_demo.py')
            env=dict(os.environ,PYTHONPATH='',PYTHONDONTWRITEBYTECODE='1')
            run=subprocess.run([sys.executable,'flight_demo.py','--ias-altitude-assist'],cwd=directory,env=env,capture_output=True,text=True,timeout=10)
        self.assertEqual(run.returncode,1)
        result=json.loads(run.stdout);self.assertIn('deploy flight_demo.py',result['reason'])
        self.assertFalse(result['pauseRequested'])

    def test_missing_paired_parallel_module_fails_before_source_admission_or_controls(self):
        source=Path(__file__).resolve().parent.parent
        with tempfile.TemporaryDirectory()as directory:
            for name in ('flight_demo.py','acquisition.py','conservative_control.py','sustain.py','checkpoint_state.py'):
                shutil.copy2(source/name,Path(directory)/name)
            env=dict(os.environ,PYTHONPATH='',PYTHONDONTWRITEBYTECODE='1')
            run=subprocess.run([sys.executable,'flight_demo.py','--sustain','--resume-acquisition','missing.json',
                '--resume-sha256','0'*64],cwd=directory,env=env,capture_output=True,text=True,timeout=10)
        self.assertEqual(run.returncode,1)
        result=json.loads(run.stdout)
        self.assertIn('parallel_snapshot',result['reason'])
        self.assertFalse(result['pauseRequested'])
        self.assertFalse(result['pauseConfirmed'])


if __name__=='__main__':unittest.main()
