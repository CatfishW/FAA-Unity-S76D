"""Synthetic protocol fixtures and simulator doubles; no real flight logs."""
import base64
import hashlib
import json
import math
from pathlib import Path
import struct

import conservative_control as c
import flight_demo as demo
from test_reliability import Api,Clock,options

PROP = demo.PROP_HANDLES
HEADING = 'sim/cockpit2/autopilot/heading_dial_deg_mag_pilot'


def f32(value):
    return struct.unpack('<f',struct.pack('<f',value))[0]


class FastClock(Clock):
    def sleep(self, seconds):
        self.now += max(1.,seconds)


class NativeModesApi(Api):
    def __init__(self, clock, ready=True):
        super().__init__(clock)
        self.values.update({name:0 for name in c.REFS})
        self.values.update({
            'sim/time/paused':1,'sim/cockpit/autopilot/autopilot_state':10 if ready else 16386,
            'sim/cockpit2/autopilot/speed_status':2 if ready else 0,
            'sim/cockpit2/autopilot/heading_status':2,
            'sim/cockpit2/autopilot/altitude_hold_status':0 if ready else 2,
            'sim/cockpit2/autopilot/altitude_hold_armed':0 if ready else 1,
            'sim/cockpit2/autopilot/autothrottle_enabled':-1 if ready else 0,
            'sim/cockpit2/autopilot/airspeed_is_mach':0,
            'sim/cockpit2/autopilot/airspeed_dial_kts_mach':100.,
            'sim/cockpit2/autopilot/sync_hold_pitch_deg':2.,
            'sim/cockpit/autopilot/flight_director_pitch':2.,
            'sim/cockpit2/autopilot/flight_director_pitch_deg':2.,
            'sim/joystick/joystick_axis_assignments':[2,1,0,4]+[0]*496,
            'sim/joystick/joy_mapped_axis_avail':[0]*81,
            'sim/joystick/joy_mapped_axis_value':[0.]*81,
            'sim/operation/override/override_planepath':[0]*20,
            demo.P+'local_vy':0.,demo.P+'true_airspeed':60.,HEADING:344.})
        self.values[PROP][1]=.64
        self.foreign=False
        self.native_start=100.
        self.heading_writes=[]

    def read(self,name,timeout=None):
        if self.foreign and name=='sim/aircraft/view/acf_ICAO':
            self.actions.append(('read',name))
            return base64.b64encode(b'C172').decode()
        if name=='sim/time/total_flight_time_sec':
            self.actions.append(('read',name));return self.native_start+self.clock.now
        if name==HEADING:
            self.actions.append(('read',name));return self.values[name]
        return super().read(name,timeout)

    def write(self,name,value):
        super().write(name,value)
        if name==HEADING:self.heading_writes.append((self.clock.now,value))

    def write_index(self,name,index,value):
        super().write_index(name,index,f32(value))

    def command(self,name,*args,**kwargs):
        super().command(name,*args,**kwargs)
        if name=='sim/autopilot/autothrottle_hard_off':
            self.values['sim/cockpit2/autopilot/autothrottle_enabled']=-1
            self.values['sim/cockpit2/autopilot/autothrottle_on']=0
            self.values['sim/cockpit2/autopilot/autothrottle_arm']=0
        elif name=='sim/autopilot/heading':self.values['sim/cockpit2/autopilot/heading_status']=2
        elif name=='sim/autopilot/level_change':
            self.values['sim/cockpit2/autopilot/speed_status']=2
            self.values['sim/cockpit2/autopilot/altitude_hold_status']=1
        elif name=='sim/autopilot/altitude_arm':
            self.values['sim/cockpit2/autopilot/altitude_hold_armed']=0
            self.values['sim/cockpit2/autopilot/altitude_hold_status']=0
            self.values['sim/cockpit/autopilot/autopilot_state']&=~32


def acquisition_checkpoint(stable=True, command=.5):
    return {'privateAcquisitionV4':False,'privateAcquisitionV5':True,
        'experimentalIasAltitudeAssist':True,'status':'completed',
        'pauseRequested':True,'pauseConfirmed':True,'nativeModesSelected':True,
        'collectiveControlIndex':0,'tailWrites':0,'aggregateWrites':0,
        'flightAccepted':False,'sustainedRouteStarted':False,'headingRouteControllerChanged':False,
        'handoffReady':stable,'sampleSequence':1,'utc':0,
        'targetAltitudeFt':2500/c.FT_TO_M,'initialMainBaselineNotLevelTrim':.5,
        'control':{'state':'stable-for-handoff'if stable else'acquiring',
            'initialMainBaselineNotLevelTrim':.5,'commandMainIndex0':command,
            'integralErrorAccumulator':False,'stableSinceNativeClock':90. if stable else None,
            'handoffReady':stable},
        'samples':[{'nativeClockBefore':99.,'nativeClockAfter':99.}],
        'finalPausedNativeReceipt':{'clockBefore':100.,'clockAfter':100.,'twoEqualClocks':True,
            'mainCollective':f32(command),'tailPropRatio':.64,'lastControllerCommand':command}}


def save(folder,data):
    folder=Path(folder);folder.mkdir(parents=True,exist_ok=True)
    path=folder/'checkpoint.json';path.write_text(json.dumps(data,indent=2))
    return path,hashlib.sha256(path.read_bytes()).hexdigest()


def orbit_checkpoint():
    orbit=c.TangentOrbit((47.651,-122.499),0,demo.ROUTE_CENTER,demo.ROUTE_LIMIT_METERS,demo.distance_bearing)
    endpoint=orbit.geo(orbit.cx+c.RADIUS_M*math.sin(math.radians(80)),orbit.cy+c.RADIUS_M*math.cos(math.radians(80)))
    samples=[];events=[]
    for sequence,native,position,true,heading,active in [
            (1,100.,(47.651,-122.499),0.,344.,False),
            (2,130.,(47.651,-122.499),0.,344.,True),
            (3,149.,endpoint,350.,334.,True)]:
        row={'sampleSequence':sequence,'nativeClockBefore':native,'nativeClockAfter':native,
            'values':{demo.P+'latitude':position[0],demo.P+'longitude':position[1],demo.P+'psi':true}}
        samples.append(row);events.append({'event':'sample',**row})
        events.append({'event':'control','sampleSequence':sequence,'nativeClock':native,
            'control':{'commandMainIndex0':.5},'headingCommand':heading,'orbitActive':active,
            'route':{'center':list(orbit.center),'radiusMeters':c.RADIUS_M}if active else None})
    events.append({'event':'terminal','status':'completed','pauseRequested':True,'pauseConfirmed':True})
    data=acquisition_checkpoint()
    data.update(privateAcquisitionV5=False,privateSustainedV6=True,sustainedRouteStarted=True,
        orbitRequested=True,orbitActive=True,orbitActivatedNativeClock=130.,orbitGateStableSinceNativeClock=100.,
        sampleCapacity=120,samplesRetained=3,sampleSequence=3,journalLines=len(events),headingWrites=2,
        headingLastCommand=334.,samples=samples,route={'kind':'tangent left orbit','center':list(orbit.center),
            'radiusMeters':c.RADIUS_M,'radialErrorMeters':0.,'desiredHeadingTrue':350.,'desiredHeadingMag':334.})
    data['control']={'state':'PD active','trim':.5,'command':.5,'commandMainIndex0':.5,
        'initialMainBaselineNotLevelTrim':.5,'integralErrorAccumulator':False,'sustainedEnvelope':True}
    data['finalPausedNativeReceipt'].update(clockBefore=150.,clockAfter=150.)
    return data,events,orbit,endpoint


def save_pair(folder,data,events):
    path,h=save(folder,data);journal=Path(folder)/'journal.jsonl'
    journal.write_text(''.join(json.dumps(event,separators=(',',':'))+'\n'for event in events))
    return path,h,journal,hashlib.sha256(journal.read_bytes()).hexdigest()


def orbit_api(clock,position):
    api=NativeModesApi(clock);api.native_start=150.
    api.values.update({demo.P+'latitude':position[0],demo.P+'longitude':position[1],
        demo.P+'psi':350.,demo.P+'mag_psi':334.,HEADING:334.})
    return api
