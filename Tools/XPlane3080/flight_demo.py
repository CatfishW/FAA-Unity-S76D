#!/usr/bin/env python3
"""Real-physics S-76 simulator demo: native heading/altitude hold and collective speed assist."""
import argparse,base64,json,math,time
from pathlib import Path
from urllib.request import Request,urlopen
from urllib.parse import urlencode
B='http://127.0.0.1:8086/api/v3'
IDS={}
def request(path,payload=None,method=None,timeout=5):
 data=None if payload is None else json.dumps(payload).encode()
 req=Request(B+path,data=data,method=method or ('GET' if data is None else 'PATCH'),headers={'Content-Type':'application/json'})
 with urlopen(req,timeout=timeout) as r:
  body=r.read();return json.loads(body) if body else None
def ident(name):
 if name not in IDS:IDS[name]=next(r['id'] for r in request('/datarefs?'+urlencode({'filter[name]':name}))['data'] if r['name']==name)
 return IDS[name]
def read(name):return request('/datarefs/'+str(ident(name))+'/value')['data']
def write(name,value):request('/datarefs/'+str(ident(name))+'/value',{'data':value})
def command(name):
 row=next(r for r in request('/commands?'+urlencode({'filter[name]':name}))['data'] if r['name']==name)
 request('/command/'+str(row['id'])+'/activate',{'duration':.1},'POST')
def clamp(x,a,b):return max(a,min(b,x))
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--start',action='store_true');p.add_argument('--seconds',type=float,default=0);a=p.parse_args()
 state_dir=Path.home()/'.local/state/faa-s76-flight';state_dir.mkdir(parents=True,exist_ok=True)
 if a.start:
  if read('sim/joystick/mouse_is_joystick'):raise SystemExit('Release mouse-flight control before starting the demo')
  command('sim/operation/pause_on')
  payload={'aircraft':{'path':'Aircraft/Laminar Research/Sikorsky S-76/S-76C.acf'},'lle_air_start':{'latitude':47.46,'longitude':-122.3,'elevation_in_meters':2500,'heading_true':0,'speed_in_meters_per_second':51.444,'pitch_in_degrees':0},'engine_status':{'all_engines':{'running':True}},'time_enum':'day','failures':{'fix_everything':True,'mean_time_between_failures_in_hours':10000}}
  request('/flight',payload,'POST',timeout=60);time.sleep(8)
  write('sim/operation/failures/enable_random_failures',0);command('sim/operation/fix_all_systems')
  command('sim/operation/pause_off');command('sim/operation/quick_start');time.sleep(.3)
  write('sim/cockpit2/switches/rotor_brake_ratio',0);write('sim/cockpit2/engine/actuators/throttle_ratio_all',1)
  write('sim/cockpit2/engine/actuators/governor_on',[1,1]+[0]*14)
  write('sim/cockpit2/controls/gear_handle_down',0);write('sim/cockpit2/controls/parking_brake_ratio',0)
  write('sim/cockpit2/engine/actuators/prop_ratio_all',.5)
  time.sleep(.5)
 if base64.b64decode(read('sim/aircraft/view/acf_ICAO')).split(b'\0')[0].decode() != 'S76':
  raise SystemExit('Flight demo only controls the installed S76 aircraft')
 heading=float(read('sim/flightmodel/position/mag_psi'))%360
 altitude=float(read('sim/cockpit2/gauges/indicators/altitude_ft_pilot'))
 if not a.start and read('sim/cockpit2/autopilot/servos_on'):
  heading=float(read('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot'))%360
  altitude=float(read('sim/cockpit2/autopilot/altitude_dial_ft'))
 target_speed=100.;collective=float(read('sim/cockpit2/engine/actuators/prop_ratio_all'));last_arm=-100.;start=last=time.monotonic();last_report=0;last_log=0
 names=['sim/flightmodel/position/latitude','sim/flightmodel/position/longitude','sim/flightmodel/position/elevation','sim/flightmodel/position/y_agl','sim/flightmodel/position/indicated_airspeed','sim/flightmodel/position/theta','sim/flightmodel/position/phi','sim/flightmodel/position/psi','sim/flightmodel/position/vh_ind','sim/flightmodel2/misc/has_crashed','sim/cockpit2/autopilot/servos_on','sim/cockpit/autopilot/autopilot_state','sim/time/paused','sim/joystick/mouse_is_joystick','sim/cockpit2/engine/indicators/N2_percent','sim/flightmodel2/engines/engine_is_burning_fuel','sim/flightmodel/weight/m_fuel_total','sim/operation/failures/enable_random_failures']
 while a.seconds<=0 or time.monotonic()-start<a.seconds:
  now=time.monotonic();dt=clamp(now-last,0,.5);last=now
  v={n:read(n) for n in names}
  if v[names[9]] or v[names[3]]<100 or abs(v[names[6]])>55 or abs(v[names[5]])>35 or v[names[13]] or min(v[names[14]][:2])<85 or max(v[names[14]][:2])>115 or v[names[15]][:2]!=[1,1] or v[names[16]]<50 or v[names[17]]:
   command('sim/operation/pause_on');print(json.dumps({'stopped':'crash/clearance/attitude/manual-input limit','values':v}),flush=True);return
  if not v[names[12]]:
   modes=int(v[names[11]])
   if now-last_arm>4 and (not v[names[10]] or not(modes&2) or not(modes&16384)):
    write('sim/cockpit2/autopilot/autopilot_electric_master',1);write('sim/cockpit2/switches/yaw_damper_on',1)
    command('sim/autopilot/servos_on');command('sim/autopilot/heading');command('sim/autopilot/altitude_hold')
    write('sim/cockpit2/autopilot/heading_dial_deg_mag_pilot',heading);write('sim/cockpit2/autopilot/altitude_dial_ft',altitude);last_arm=now
   error=target_speed-max(0,float(v[names[4]]));collective=clamp(collective+clamp(error*.0007,-.018,.018)*dt,.15,.85)
   write('sim/cockpit2/engine/actuators/prop_ratio_all',collective)
  if now-last_report>=1:
   report={'utc':time.time(),'controller':'native AP heading/altitude + collective assist','targetSpeedKt':target_speed,'targetAltitudeFt':altitude,'targetHeadingMag':heading,'collective':collective,'values':v}
   (state_dir/'latest.json').write_text(json.dumps(report,indent=2));last_report=now
   if now-last_log>=10:
    print(json.dumps(report),flush=True);last_log=now
  time.sleep(.1)
if __name__=='__main__':main()
