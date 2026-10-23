using UnityEngine;
using System;

namespace AviationUI
{
    /// <summary>
    /// MonoBehaviour wrapper for AviationFlightData to use in scene
    /// </summary>
    public class AviationFlightDataProvider : MonoBehaviour
    {
        [SerializeField]
        private AviationFlightData _flightData = new AviationFlightData();

        public AviationFlightData FlightData => _flightData;

        /// <summary>
        /// Event fired when flight data is updated
        /// </summary>
        public event Action<AviationFlightData> OnFlightDataUpdated;

        /// <summary>
        /// Update the flight data and notify listeners
        /// </summary>
        public void UpdateFlightData(AviationFlightData newData)
        {
            _flightData = newData;
            OnFlightDataUpdated?.Invoke(_flightData);
        }

        /// <summary>
        /// Set individual values (for external system integration)
        /// </summary>
        public void SetHeading(float heading) { _flightData.heading = heading; NotifyUpdate(); }
        public void SetPitch(float pitch) { _flightData.pitch = pitch; NotifyUpdate(); }
        public void SetRoll(float roll) { _flightData.roll = roll; NotifyUpdate(); }
        public void SetAirspeed(float airspeed) { _flightData.indicatedAirspeed = airspeed; NotifyUpdate(); }
        public void SetAltitude(float altitude) { _flightData.altitudeMSL = altitude; NotifyUpdate(); }
        public void SetVerticalSpeed(float vs) { _flightData.verticalSpeed = vs; NotifyUpdate(); }
        public void SetAltitudeAGL(float agl) { _flightData.altitudeAGL = agl; NotifyUpdate(); }
        public void SetEngine1Torque(float torque) { _flightData.engine1Torque = torque; NotifyUpdate(); }
        public void SetEngine2Torque(float torque) { _flightData.engine2Torque = torque; NotifyUpdate(); }
        public void SetEngine1NR(float nr) { _flightData.engine1NR = nr; NotifyUpdate(); }
        public void SetEngine2NR(float nr) { _flightData.engine2NR = nr; NotifyUpdate(); }

        private void NotifyUpdate()
        {
            OnFlightDataUpdated?.Invoke(_flightData);
        }
    }
}
