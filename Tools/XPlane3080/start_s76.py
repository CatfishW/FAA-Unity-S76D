#!/usr/bin/env python3
"""Explicitly reset the simulator to a parked S-76C at KSEA, then run its engines with the rotor brake released."""
import argparse
import json
from pathlib import Path
import time
from urllib.parse import urlencode
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--xplane-root', type=Path, default=Path.home()/'.steam/debian-installation/steamapps/common/X-Plane 12')
    parser.add_argument('--api', default='http://127.0.0.1:8086/api/v3')
    parser.add_argument('--reset', action='store_true', help='Start a new flight; replaces the current simulator flight')
    args = parser.parse_args()
    root = args.xplane_root.resolve(strict=True)
    aircraft = 'Aircraft/Laminar Research/Sikorsky S-76/S-76C.acf'
    if not (root/aircraft).is_file():
        raise SystemExit('Installed S-76C not found')

    def request(path, method='GET', payload=None):
        data = None if payload is None else json.dumps(payload).encode()
        req = Request(args.api.rstrip('/')+path, data=data, method=method, headers={'Content-Type':'application/json'})
        with urlopen(req, timeout=15) as response:
            body = response.read()
            return json.loads(body) if body else None

    def identifier(name):
        rows = request('/datarefs?'+urlencode({'filter[name]':name}))['data']
        return next(row['id'] for row in rows if row['name']==name)

    def read(name):
        return request('/datarefs/'+str(identifier(name))+'/value')['data']

    def write(name, value):
        request('/datarefs/'+str(identifier(name))+'/value','PATCH',{'data':value})

    def command(name, duration=0):
        rows=request('/commands?'+urlencode({'filter[name]':name}))['data']
        command_id=next(row['id'] for row in rows if row['name']==name)
        request('/command/'+str(command_id)+'/activate','POST',{'duration':duration})

    if not args.reset:
        print(json.dumps({'ready':True,'aircraft':aircraft,'reset_requested':False,'mouse_flight_active':read('sim/joystick/mouse_is_joystick')}))
        return
    if read('sim/joystick/mouse_is_joystick'):
        raise SystemExit('Release mouse-flight control in X-Plane before resetting; no flight changed.')
    command('sim/operation/pause_on')
    command('sim/operation/reload_aircraft_no_art')
    time.sleep(8)
    request('/flight','POST',{'aircraft':{'path':aircraft},'runway_start':{'airport_id':'KSEA','runway':'16L'},
        'engine_status':{'all_engines':{'running':False}},'time_enum':'day',
        'failures':{'fix_everything':True,'mean_time_between_failures_in_hours':10000}})
    # The installed cached S-76/scenery loads asynchronously. Keep engines off
    # while it loads, then verify the actual aircraft path before starting.
    write('sim/operation/failures/enable_random_failures',0)
    command('sim/operation/pause_off')
    time.sleep(8)
    import base64
    loaded=base64.b64decode(read('sim/aircraft/view/acf_relative_path')).split(b'\0')[0].decode()
    if loaded != aircraft:
        command('sim/operation/pause_on')
        raise SystemExit('Requested aircraft load not confirmed; simulator left paused.')
    command('sim/operation/pause_on')
    write('sim/operation/failures/enable_random_failures',0)
    write('sim/operation/failures/mean_time_between_failure_hrs',10000.0)
    command('sim/operation/fix_all_systems')
    for attempt in range(20):
        if read('sim/operation/failures/enable_random_failures') == 0:
            break
        time.sleep(.1)
    else:
        raise SystemExit('Random failure configuration not confirmed; simulator left paused.')
    command('sim/operation/pause_off')
    command('sim/operation/quick_start', .2)
    time.sleep(.4)
    write('sim/cockpit2/switches/rotor_brake_ratio',0.0)
    write('sim/cockpit2/controls/parking_brake_ratio',1.0)
    write('sim/cockpit2/engine/actuators/throttle_ratio_all',1.0)
    print('S-76C reset at KSEA; engines running, rotor brake released, parking brake on. Checking stability...',flush=True)
    for i in range(15):
        roll=read('sim/flightmodel/position/phi')
        rpm=read('sim/cockpit2/engine/indicators/N2_percent')[:2]
        crashed=read('sim/flightmodel2/misc/has_crashed')
        mouse=read('sim/joystick/mouse_is_joystick')
        burning=read('sim/flightmodel2/engines/engine_is_burning_fuel')[:2]
        if abs(roll)>8 or max(rpm)>115 or crashed or mouse or i>=5 and (min(rpm)<90 or burning != [1,1]):
            command('sim/operation/pause_on')
            raise SystemExit('Instability or mouse-flight input detected; simulator paused.')
        time.sleep(1)
    print('Parked stability check passed. This launcher provides no takeoff or automatic flight control.')


if __name__=='__main__':
    main()
