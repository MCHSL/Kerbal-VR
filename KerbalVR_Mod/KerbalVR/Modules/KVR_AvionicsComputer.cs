﻿using UnityEngine;
using Valve.VR;

namespace KerbalVR.Modules {
    /// <summary>
    /// A part module that manages data flow to the vessel,
    /// and other modules. Start up on Flight only.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KVR_AvionicsComputer : MonoBehaviour {

        #region Private Members
        protected SteamVR_Action_Vector2 controlFlightStick;
        protected bool isInitialized = false;
        #endregion

        protected void Initialize() {
            if (isInitialized) return;
            controlFlightStick = SteamVR_Input.GetVector2Action("default", "FlightStick");

            isInitialized = true;
            Utils.Log("KVR_AvionicsComputer initialized");
        }

        protected void Start() {
            Utils.Log("KVR_AvionicsComputer Start");
            FlightGlobals.ActiveVessel.OnFlyByWire += VesselControl;
        }

        protected void OnEnable() {
            Utils.Log("KVR_AvionicsComputer OnEnable");
            // FlightGlobals.ActiveVessel.OnFlyByWire += VesselControl;
        }

        protected void OnDisable() {
            Utils.Log("KVR_AvionicsComputer OnDisable");
            // FlightGlobals.ActiveVessel.OnFlyByWire -= VesselControl;
        }

        protected void OnDestroy() {
            Utils.Log("KVR_AvionicsComputer shutting down...");
            FlightGlobals.ActiveVessel.OnFlyByWire -= VesselControl;
        }

        protected void Update() {
            if (!isInitialized && KerbalVR.Core.IsOpenVrReady) {
                Initialize();
            }
            if (!isInitialized) {
                return;
            }

            // Utils.Log("Avionics Update: " + controlFlightStick.changed);
        }

        protected void VesselControl(FlightCtrlState state) {
            // do nothing without the OpenVR input system
            if (!isInitialized) {
                return;
            }

            bool isControllingVessel = false;
            float commandYaw = 0f;
            float commandPitch = 0f;
            float commandRoll = 0f;

            // get flight stick inputs
            if (controlFlightStick.axis != Vector2.zero) {
                Vector2 stickPos = controlFlightStick.GetAxis(SteamVR_Input_Sources.Any);
                commandPitch = stickPos.y;
                if (KerbalVR.Configuration.Instance.SwapYawRollControls) {
                    commandYaw = stickPos.x;
                } else {
                    commandRoll = stickPos.x;
                }
                isControllingVessel = true;
            }

            // get yaw stick inputs

            // only actuate the vessel control if player is commanding inputs on the controller
            if (isControllingVessel) {
                state.yaw = commandYaw;
                state.pitch = commandPitch;
                state.roll = commandRoll;
            }
        }

    } // class KVR_AvionicsComputer
} // namespace KerbalVR