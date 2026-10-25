#!/usr/bin/env python3
"""Sample the real native-API controller report; never changes simulator state."""
import argparse,json,math,time
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--seconds',type=float,default=120);a=p.parse_args()
path=Path.home()/'.local/state/faa-s76-flight/latest.json';rows=[];errors=[];start=time.monotonic()
while time.monotonic()-start<a.seconds:
 try:r=json.loads(path.read_text())
 except (OSError,json.JSONDecodeError):time.sleep(.1);continue
 v=r['values'];age=time.time()-r['utc']
 if age>3:errors.append('stale controller/native sample')
 if v['sim/flightmodel2/misc/has_crashed'] or v['sim/time/paused']:errors.append('crashed or paused')
 if v['sim/flightmodel/position/y_agl']<500:errors.append('clearance below 500m')
 if not 80<=v['sim/flightmodel/position/indicated_airspeed']<=120:errors.append('airspeed outside 80..120kt')
 if abs(v['sim/flightmodel/position/phi'])>25 or abs(v['sim/flightmodel/position/theta'])>20:errors.append('attitude outside demo bounds')
 if v['sim/flightmodel2/engines/engine_is_burning_fuel'][:2]!=[1,1]:errors.append('engine not running')
 if v['sim/operation/failures/enable_random_failures']:errors.append('random failures enabled')
 if not rows or r['utc']!=rows[-1]['utc']:rows.append(r)
 time.sleep(1)
def extent(key,scale=1):return [min(r['values'][key] for r in rows)*scale,max(r['values'][key] for r in rows)*scale]
v0,v1=rows[0]['values'],rows[-1]['values'];lat0,lat1=map(math.radians,[v0['sim/flightmodel/position/latitude'],v1['sim/flightmodel/position/latitude']]);dl=math.radians(v1['sim/flightmodel/position/longitude']-v0['sim/flightmodel/position/longitude']);dlat=lat1-lat0
h=math.sin(dlat/2)**2+math.cos(lat0)*math.cos(lat1)*math.sin(dl/2)**2
distance=2*6371000*math.asin(min(1,math.sqrt(h)))
print(json.dumps({'passed':not errors,'seconds':round(time.monotonic()-start,1),'samples':len(rows),'errors':sorted(set(errors)),'ias_kt':extent('sim/flightmodel/position/indicated_airspeed'),'altitude_msl_ft':extent('sim/flightmodel/position/elevation',1/.3048),'agl_m':extent('sim/flightmodel/position/y_agl'),'pitch_deg':extent('sim/flightmodel/position/theta'),'roll_deg':extent('sim/flightmodel/position/phi'),'distance_m':distance},indent=2))
raise SystemExit(bool(errors))
