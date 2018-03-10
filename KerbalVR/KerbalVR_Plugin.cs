using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace KerbalVR
{
    // Start plugin on entering the Flight scene
    //
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class KerbalVR_Plugin : MonoBehaviour
    {
        // this function allows importing DLLs from a given path
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        // define location of OpenVR library
        public static string OpenVRDllPath {
            get {
                string currentPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string openVrPath = Path.Combine(currentPath, "openvr");
                return Path.Combine(openVrPath, Utils.Is64BitProcess ? "win64" : "win32");
            }
        }

        private bool hmdIsInitialized = false;
        private bool hmdIsAllowed = false;
        private bool hmdIsAllowed_prev = false;

        private bool renderToScreen = true;

        private CVRSystem vrSystem;
        private CVRCompositor vrCompositor;
        private TrackedDevicePose_t[] vrDevicePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private TrackedDevicePose_t[] vrRenderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private TrackedDevicePose_t[] vrGamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        private VRControllerState_t ctrlStateLeft = new VRControllerState_t();
        private VRControllerState_t ctrlStateRight = new VRControllerState_t();
        private uint ctrlStateLeft_lastPacketNum, ctrlStateRight_lastPacketNum;
        private uint ctrlIndexLeft = 0;
        private uint ctrlIndexRight = 0;

        private Texture_t hmdLeftEyeTexture, hmdRightEyeTexture;
        private VRTextureBounds_t hmdTextureBounds;
        private RenderTexture hmdLeftEyeRenderTexture, hmdRightEyeRenderTexture;

        // define controller button masks
        //--------------------------------------------------------------

        // trigger button
        public const ulong CONTROLLER_BUTTON_MASK_TRIGGER = 1ul << (int)EVRButtonId.k_EButton_SteamVR_Trigger;

        // app menu button
        public const ulong CONTROLLER_BUTTON_MASK_APP_MENU = 1ul << (int)EVRButtonId.k_EButton_ApplicationMenu;

        // touchpad
        public const ulong CONTROLLER_BUTTON_MASK_TOUCHPAD = 1ul << (int)EVRButtonId.k_EButton_SteamVR_Touchpad;

        // list of all cameras in the game
        //--------------------------------------------------------------
        private string[] cameraNames = new string[7]
        {
            "GalaxyCamera",
            "Camera ScaledSpace",
            "Camera 01",
            "Camera 00",
            "InternalCamera",
            "UIMainCamera",
            "UIVectorCamera",
        };

        // list of cameras to render (string names), defined on Start()
        private List<string> cameraNamesToRender;

        // struct to keep track of Camera properties
        private struct CameraProperties
        {
            public Camera camera;
            public Matrix4x4 originalProjMatrix;
            public Matrix4x4 hmdLeftProjMatrix;
            public Matrix4x4 hmdRightProjMatrix;

            public CameraProperties(Camera camera, Matrix4x4 originalProjMatrix, Matrix4x4 hmdLeftProjMatrix, Matrix4x4 hmdRightProjMatrix) {
                this.camera = camera;
                this.originalProjMatrix = originalProjMatrix;
                this.hmdLeftProjMatrix = hmdLeftProjMatrix;
                this.hmdRightProjMatrix = hmdRightProjMatrix;
            }
        }

        // list of cameras to render (Camera objects)
        private List<CameraProperties> camerasToRender;

        /// <summary>
        /// Overrides the Start method for a MonoBehaviour plugin.
        /// </summary>
        void Start() {
            Utils.LogInfo("KerbalVrPlugin started.");

            // define what cameras to render to HMD
            cameraNamesToRender = new List<string>();
            cameraNamesToRender.Add(cameraNames[0]); // renders the galaxy
            cameraNamesToRender.Add(cameraNames[1]); // renders space/planets?
            cameraNamesToRender.Add(cameraNames[2]); // renders things far away (like out to the horizon)
            cameraNamesToRender.Add(cameraNames[3]); // renders things close to you
            cameraNamesToRender.Add(cameraNames[4]); // renders the IVA view (cockpit)
            //cameraNamesToRender.Add(cameraNames[5]); // don't render UI, it looks shitty
            //cameraNamesToRender.Add(cameraNames[6]); // don't render UI, it looks shitty

            camerasToRender = new List<CameraProperties>(cameraNamesToRender.Count);

            // define the vessel to control
            //Vessel activeVessel = FlightGlobals.ActiveVessel;
            //activeVessel.OnFlyByWire += VesselControl;

            DontDestroyOnLoad(this);
        }

        /// <summary>
        /// Overrides the Update method, called every frame.
        /// </summary>
        void LateUpdate() {
            try {

                // do nothing unless we are in IVA
                hmdIsAllowed = (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA);

                // start HMD using the Y key
                if (Input.GetKeyDown(KeyCode.Y) && hmdIsAllowed) {
                    if (!hmdIsInitialized) {
                        Utils.LogInfo("Initializing HMD...");
                        try {
                            bool retVal = InitHMD();
                            if (retVal) {
                                Utils.LogInfo("HMD initialized.");
                            }
                        } catch (Exception e) {
                            Utils.LogError(e.Message);
                        }
                    } else {
                        ResetInitialHmdPosition();
                    }
                }

                // perform regular updates if HMD is initialized
                if (hmdIsAllowed && hmdIsInitialized) {
                    EVRCompositorError vrCompositorError = EVRCompositorError.None;

                    // get latest HMD pose
                    //--------------------------------------------------------------
                    vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseSeated, 0.0f, vrDevicePoses);
                    HmdMatrix34_t vrLeftEyeTransform = vrSystem.GetEyeToHeadTransform(EVREye.Eye_Left);
                    HmdMatrix34_t vrRightEyeTransform = vrSystem.GetEyeToHeadTransform(EVREye.Eye_Right);
                    vrCompositorError = vrCompositor.WaitGetPoses(vrRenderPoses, vrGamePoses);

                    if (vrCompositorError != EVRCompositorError.None) {
                        Utils.LogWarning("WaitGetPoses failed: " + (int)vrCompositorError);
                        return;
                    }

                    // convert SteamVR poses to Unity coordinates
                    var hmdTransform = new SteamVR_Utils.RigidTransform(vrDevicePoses[OpenVR.k_unTrackedDeviceIndex_Hmd].mDeviceToAbsoluteTracking);
                    var hmdLeftEyeTransform = new SteamVR_Utils.RigidTransform(vrLeftEyeTransform);
                    var hmdRightEyeTransform = new SteamVR_Utils.RigidTransform(vrRightEyeTransform);
                    var ctrlPoseLeft = new SteamVR_Utils.RigidTransform(vrDevicePoses[ctrlIndexLeft].mDeviceToAbsoluteTracking);
                    var ctrlPoseRight = new SteamVR_Utils.RigidTransform(vrDevicePoses[ctrlIndexRight].mDeviceToAbsoluteTracking);



                    // Render the LEFT eye
                    //--------------------------------------------------------------
                    // rotate camera according to the HMD orientation
                    InternalCamera.Instance.transform.localRotation = hmdTransform.rot;

                    // translate the camera to match the position of the left eye, from origin
                    InternalCamera.Instance.transform.localPosition = new Vector3(0f, 0f, 0f);
                    InternalCamera.Instance.transform.Translate(hmdLeftEyeTransform.pos);

                    // translate the camera to match the position of the HMD
                    InternalCamera.Instance.transform.localPosition += hmdTransform.pos;

                    // move the FlightCamera to match the position of the InternalCamera (so the outside world moves accordingly)
                    FlightCamera.fetch.transform.position = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.position);
                    FlightCamera.fetch.transform.rotation = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.rotation);

                    // render the set of cameras
                    foreach (CameraProperties camStruct in camerasToRender) {
                        // set projection matrix
                        camStruct.camera.projectionMatrix = camStruct.hmdLeftProjMatrix;

                        // set texture to render to
                        camStruct.camera.targetTexture = hmdLeftEyeRenderTexture;

                        // render camera
                        camStruct.camera.Render();
                    }


                    // Render the RIGHT eye (see previous comments)
                    //--------------------------------------------------------------
                    InternalCamera.Instance.transform.localRotation = hmdTransform.rot;
                    InternalCamera.Instance.transform.localPosition = new Vector3(0f, 0f, 0f);
                    InternalCamera.Instance.transform.Translate(hmdRightEyeTransform.pos);
                    InternalCamera.Instance.transform.localPosition += hmdTransform.pos;
                    FlightCamera.fetch.transform.position = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.position);
                    FlightCamera.fetch.transform.rotation = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.rotation);

                    foreach (CameraProperties camStruct in camerasToRender) {
                        camStruct.camera.projectionMatrix = camStruct.hmdRightProjMatrix;
                        camStruct.camera.targetTexture = hmdRightEyeRenderTexture;
                        camStruct.camera.Render();
                    }

                    try {
                        // Set camera position to an HMD-centered position (for regular screen rendering)
                        //--------------------------------------------------------------
                        if (renderToScreen) {
                            foreach (CameraProperties camStruct in camerasToRender) {
                                camStruct.camera.targetTexture = null;
                                camStruct.camera.projectionMatrix = camStruct.originalProjMatrix;
                            }
                            InternalCamera.Instance.transform.localRotation = hmdTransform.rot;
                            InternalCamera.Instance.transform.localPosition = hmdTransform.pos;
                            FlightCamera.fetch.transform.position = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.position);
                            FlightCamera.fetch.transform.rotation = InternalSpace.InternalToWorld(InternalCamera.Instance.transform.rotation);
                        }


                        // Submit frames to HMD
                        //--------------------------------------------------------------
                        vrCompositorError = vrCompositor.Submit(EVREye.Eye_Left, ref hmdLeftEyeTexture, ref hmdTextureBounds, EVRSubmitFlags.Submit_Default);
                        if (vrCompositorError != EVRCompositorError.None) {
                            Utils.LogWarning("Submit (Eye_Left) failed: " + (int)vrCompositorError);
                        }

                        vrCompositorError = vrCompositor.Submit(EVREye.Eye_Right, ref hmdRightEyeTexture, ref hmdTextureBounds, EVRSubmitFlags.Submit_Default);
                        if (vrCompositorError != EVRCompositorError.None) {
                            Utils.LogWarning("Submit (Eye_Right) failed: " + (int)vrCompositorError);
                        }

                        vrCompositor.PostPresentHandoff();

                        //  GL.Flush();

                    } catch (Exception e) {
                        Utils.LogError("Exception! " + e.ToString());
                    }

                    // disable highlighting of parts due to mouse
                    // TODO: there needs to be a better way to do this. this affects the Part permanently
                    Part hoveredPart = Mouse.HoveredPart;
                    if (hoveredPart != null) {
                        hoveredPart.HighlightActive = false;
                        hoveredPart.highlightColor.a = 0f;// = new Color(0f, 0f, 0f, 0f);
                                                          //Utils.LogInfo("hovered part: " + hoveredPart.name);
                    }


                    // DEBUG
                    if (Input.GetKeyDown(KeyCode.O)) {
                        Utils.LogInfo("POSITION hmdTransform : " + hmdTransform.pos.ToString("F3"));
                        Utils.LogInfo("POSITION hmdLTransform : " + hmdLeftEyeTransform.pos.ToString("F3"));
                        Utils.LogInfo("POSITION hmdRTransform : " + hmdRightEyeTransform.pos.ToString("F3"));

                        Utils.LogInfo("POSITION InternalCamera.transform.abs : " + InternalCamera.Instance.transform.position.ToString("F3"));
                        Utils.LogInfo("POSITION InternalCamera.transform.rel : " + InternalCamera.Instance.transform.localPosition.ToString("F3"));

                        /*foreach (Camera c in Camera.allCameras) {
                            Utils.LogInfo("Camera: " + c.name + ", cullingMask = " + c.cullingMask);
                        }*/

                    }
                }

                // if we are exiting VR, restore the cameras
                if (!hmdIsAllowed && hmdIsAllowed_prev) {
                    foreach (CameraProperties camStruct in camerasToRender) {
                        camStruct.camera.projectionMatrix = camStruct.originalProjMatrix;
                        camStruct.camera.targetTexture = null;
                    }
                }

            } catch (Exception e) {
                Utils.LogError(e.ToString());
            }

            hmdIsAllowed_prev = hmdIsAllowed;
        }

        public void VesselControl(FlightCtrlState s) {
            if (hmdIsAllowed && hmdIsInitialized) {

                // handle left controller inputs
                //--------------------------------------------------------------
                bool ctrlStateOk = vrSystem.GetControllerState(ctrlIndexLeft, ref ctrlStateLeft, 1);
                if (ctrlStateOk && ctrlStateLeft.unPacketNum != ctrlStateLeft_lastPacketNum) {
                    // activate next stage with app menu button
                    if ((ctrlStateLeft.ulButtonPressed & CONTROLLER_BUTTON_MASK_APP_MENU) > 0) {
                        KSP.UI.Screens.StageManager.ActivateNextStage();
                    }

                    // control the throttle by touching the touchpad (no need to click)
                    if ((ctrlStateLeft.ulButtonTouched & CONTROLLER_BUTTON_MASK_TOUCHPAD) > 0) {
                        float throttleCmd = Mathf.Clamp((ctrlStateLeft.rAxis0.y + 1.0f) / 2.0f, 0.0f, 1.0f);
                        s.mainThrottle = throttleCmd;
                    }

                    ctrlStateLeft_lastPacketNum = ctrlStateLeft.unPacketNum;
                }

                // handle right controller inputs
                //--------------------------------------------------------------
                ctrlStateOk = vrSystem.GetControllerState(ctrlIndexRight, ref ctrlStateRight, 1);
                if (ctrlStateOk && ctrlStateRight.unPacketNum != ctrlStateRight_lastPacketNum) {
                    // control pitch and roll by touching the touchpad (no need to click)
                    if ((ctrlStateRight.ulButtonTouched & CONTROLLER_BUTTON_MASK_TOUCHPAD) > 0) {
                        float pitchCmd = -ctrlStateRight.rAxis0.y;
                        float rollCmd = ctrlStateRight.rAxis0.x;
                        s.pitch = pitchCmd;
                        s.roll = rollCmd;
                    }

                    // brake with the trigger
                    /*if ((ctrlStateRight.ulButtonPressed & CONTROLLER_BUTTON_MASK_TRIGGER) > 0)
                    {
                        FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true);
                    }
                    else
                    {
                        FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false);
                    }*/

                    ctrlStateRight_lastPacketNum = ctrlStateRight.unPacketNum;
                }
            }

            // feed inputs to vessel
            FlightInputHandler.state = s;
        }


        /// <summary>
        /// Overrides the OnDestroy method, called when plugin is destroyed (leaving Flight scene).
        /// </summary>
        void OnDestroy() {
            Utils.LogInfo("KerbalVrPlugin OnDestroy");
            OpenVR.Shutdown();
            hmdIsInitialized = false;
        }

        /// <summary>
        /// Initialize HMD using OpenVR API calls.
        /// </summary>
        /// <returns>True on success, false otherwise. Errors logged.</returns>
        bool InitHMD() {
            bool retVal = false;

            // return if HMD has already been initialized
            if (hmdIsInitialized) {
                return true;
            }

            // set the location of the OpenVR DLL
            SetDllDirectory(OpenVRDllPath);

            // check if HMD is connected on the system
            retVal = OpenVR.IsHmdPresent();
            if (!retVal) {
                Utils.LogError("HMD not found on this system.");
                return retVal;
            }

            // check if SteamVR runtime is installed.
            // For this plugin, MAKE SURE IT IS ALREADY RUNNING.
            retVal = OpenVR.IsRuntimeInstalled();
            if (!retVal) {
                Utils.LogError("SteamVR runtime not found on this system.");
                return retVal;
            }

            // initialize HMD
            EVRInitError hmdInitErrorCode = EVRInitError.None;
            vrSystem = OpenVR.Init(ref hmdInitErrorCode, EVRApplicationType.VRApplication_Scene);

            // return if failure
            retVal = (hmdInitErrorCode == EVRInitError.None);
            if (!retVal) {
                Utils.LogError("Failed to initialize HMD. Init returned: " + OpenVR.GetStringForHmdError(hmdInitErrorCode));
                return retVal;
            } else {
                Utils.LogInfo("OpenVR.Init passed.");
            }

            // reset "seated position" and capture initial position. this means you should hold the HMD in
            // the position you would like to consider "seated", before running this code.

            ResetInitialHmdPosition();

            // initialize Compositor
            vrCompositor = OpenVR.Compositor;

            // initialize render textures (for displaying on HMD)
            uint renderTextureWidth = 0;
            uint renderTextureHeight = 0;
            vrSystem.GetRecommendedRenderTargetSize(ref renderTextureWidth, ref renderTextureHeight);
            //renderTextureWidth /= 2;
            //renderTextureHeight /= 2;

            Utils.LogInfo("Render Texture size: " + renderTextureWidth + " x " + renderTextureHeight);

            hmdLeftEyeRenderTexture = new RenderTexture((int)renderTextureWidth, (int)renderTextureHeight, 24, RenderTextureFormat.ARGB32);
            hmdLeftEyeRenderTexture.Create();

            hmdRightEyeRenderTexture = new RenderTexture((int)renderTextureWidth, (int)renderTextureHeight, 24, RenderTextureFormat.ARGB32);
            hmdRightEyeRenderTexture.Create();

            hmdLeftEyeTexture.handle = hmdLeftEyeRenderTexture.GetNativeTexturePtr();
            hmdLeftEyeTexture.eColorSpace = EColorSpace.Auto;

            hmdRightEyeTexture.handle = hmdRightEyeRenderTexture.GetNativeTexturePtr();
            hmdRightEyeTexture.eColorSpace = EColorSpace.Auto;


            switch (SystemInfo.graphicsDeviceType) {
                case UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore:
                case UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2:
                case UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3:
                    hmdLeftEyeTexture.eType = ETextureType.OpenGL;
                    hmdRightEyeTexture.eType = ETextureType.OpenGL;
                    break; //doesnt work in unity 5.4 with current SteamVR (12/2016)
                case UnityEngine.Rendering.GraphicsDeviceType.Direct3D9:
                    throw (new Exception("DirectX9 not supported"));
                case UnityEngine.Rendering.GraphicsDeviceType.Direct3D11:
                case UnityEngine.Rendering.GraphicsDeviceType.Direct3D12:
                    hmdLeftEyeTexture.eType = ETextureType.DirectX;
                    hmdRightEyeTexture.eType = ETextureType.DirectX;
                    break;
                default:
                    throw (new Exception(SystemInfo.graphicsDeviceType.ToString() + " not supported"));
            }

            // Set rendering bounds on texture to render?
            // I assume min=0.0 and max=1.0 renders to the full extent of the texture
            hmdTextureBounds.uMin = 0.0f;
            hmdTextureBounds.uMax = 1.0f;
            hmdTextureBounds.vMin = 1.0f; // flip the vertical coordinate for some reason
            hmdTextureBounds.vMax = 0.0f;

            // TODO: Need to understand better how to create render targets and incorporate hidden area mask mesh

            foreach (Camera camera in Camera.allCameras) {
                Utils.LogInfo("KSP Camera: " + camera.name);
            }

            // search for camera objects to render
            foreach (string cameraName in cameraNamesToRender) {
                foreach (Camera camera in Camera.allCameras) {
                    if (cameraName.Equals(camera.name)) {
                        float nearClipPlane = (camera.name.Equals(cameraNames[3])) ? 0.05f : camera.nearClipPlane;

                        HmdMatrix44_t projLeft = vrSystem.GetProjectionMatrix(EVREye.Eye_Left, nearClipPlane, camera.farClipPlane);
                        HmdMatrix44_t projRight = vrSystem.GetProjectionMatrix(EVREye.Eye_Right, nearClipPlane, camera.farClipPlane);
                        //HmdMatrix44_t projLeft = vrSystem.GetProjectionMatrix(EVREye.Eye_Left, nearClipPlane, camera.farClipPlane, EGraphicsAPIConvention.API_DirectX); // this doesn't seem to work
                        //HmdMatrix44_t projRight = vrSystem.GetProjectionMatrix(EVREye.Eye_Right, nearClipPlane, camera.farClipPlane, EGraphicsAPIConvention.API_DirectX); // this doesn't seem to work
                        camerasToRender.Add(new CameraProperties(camera, camera.projectionMatrix, MathUtils.Matrix4x4_OpenVr2UnityFormat(ref projLeft), MathUtils.Matrix4x4_OpenVr2UnityFormat(ref projRight)));
                        break;
                    }
                }
            }

            // detect controllers
            for (uint idx = 0; idx < OpenVR.k_unMaxTrackedDeviceCount; idx++) {
                if ((ctrlIndexLeft == 0) && (vrSystem.GetTrackedDeviceClass(idx) == ETrackedDeviceClass.Controller)) {
                    ctrlIndexLeft = idx;
                } else if ((ctrlIndexRight == 0) && (vrSystem.GetTrackedDeviceClass(idx) == ETrackedDeviceClass.Controller)) {
                    ctrlIndexRight = idx;
                }
            }

            hmdIsInitialized = true;

            return retVal;
        }

        /// <summary>
        /// Sets the current real-world position of the HMD as the seated origin in IVA.
        /// </summary>
        void ResetInitialHmdPosition() {
            if (hmdIsInitialized) {
                vrSystem.ResetSeatedZeroPose();
                Utils.LogInfo("Seated pose reset!");
            }
        }

    } // class KerbalVR_Plugin
} // namespace KerbalVR