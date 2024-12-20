﻿using OpenVR.NET.Devices;
using OpenVR.NET.Manifest;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valve.VR;
using static OpenVR.NET.Extensions;

namespace OpenVR.NET;

/// <summary>
/// A multithreaded, object oriented wrapper around OpenVR.
/// Left handed coordinate system - Y is up, X is right, Z is forward.
/// </summary>
public class VR
{
	public VrState State { get; private set; } = VrState.NotInitialized;

	public readonly VrEvents Events = new();

	private Chaperone? chaperone;
	/// <inheritdoc cref="IChaperone"/>
	public IChaperone Chaperone => chaperone ??= new( this );

	private object[]? interfaces; // this is used to prevent a race condition while initializing openvr interfaces on other threads

	/// <summary>
	/// Tries to initialize OpenVR.
	/// This call is blocking and might take several seconds to complete.
	/// You should call this on a separate woker thread in order not to block other threads
	/// </summary>
	public bool TryStart(EVRApplicationType appType = EVRApplicationType.VRApplication_Scene)
	{
		if (State.HasFlag(VrState.NotInitialized))
		{
			EVRInitError error = EVRInitError.None;
			CVR = Valve.VR.OpenVR.Init(ref error, appType);
			if (error is EVRInitError.None)
			{
				interfaces = new object[]
				{
					Valve.VR.OpenVR.Applications,
					Valve.VR.OpenVR.Chaperone,
					Valve.VR.OpenVR.ChaperoneSetup,
					Valve.VR.OpenVR.Compositor,
					Valve.VR.OpenVR.Debug,
					Valve.VR.OpenVR.ExtendedDisplay,
					Valve.VR.OpenVR.HeadsetView,
					Valve.VR.OpenVR.Input,
					Valve.VR.OpenVR.IOBuffer,
					Valve.VR.OpenVR.Notifications,
					Valve.VR.OpenVR.Overlay,
					Valve.VR.OpenVR.OverlayView,
					Valve.VR.OpenVR.RenderModels,
					Valve.VR.OpenVR.Screenshots,
					Valve.VR.OpenVR.Settings,
					Valve.VR.OpenVR.SpatialAnchors,
					Valve.VR.OpenVR.System,
					Valve.VR.OpenVR.TrackedCamera
				};
				State = VrState.OK;
				drawContext = new(this);
				Events.Log("OpenVR initialized succesfuly", EventType.InitializationSuccess, VrState.OK);
			}
			else
			{
				Events.Log("OpenVR could not be initialized", EventType.InitializationError, error);
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Exits VR. You need to make sure <see cref="UpdateDraw"/>, <see cref="UpdateInput"/> nor <see cref="Update"/> are running
	/// and you're not loading any models before making this call. Please be aware that if you do not terminate the running process,
	/// the user will not return to the home scene, but will be left in the vr limbo until they launch another program (it will not terminate yours)
	/// </summary>
	public void Exit()
	{
		interfaces = null;
		CVR = null!;
		Valve.VR.OpenVR.Shutdown();
	}

	/// <summary>
	/// When the <see cref="EVREventType.VREvent_Quit"/> event is received, call this to extend time
	/// before OpenVR termintes the process
	/// </summary>
	public void GracefullyExit()
	{
		CVR.AcknowledgeQuit_Exiting();
	}

	/// <summary>
	/// Installs the app/updates the vrmanifest file. If a path is provided,
	/// the .vrmanifest file will be saved there, otherwise it will be saved to the current directory.
	/// Returns the absolute path to the .vrmanifest file, or <see langword="null"/> if the installation failed.
	/// This has to be called after initializing vr.
	/// Note that updating the manifest might require a restart of OpenVR Runtime or the OS, as some values, such as images might be cached
	/// </summary>
	public string? InstallApp(VrManifest manifest, string? path = null)
	{
		var json = JsonSerializer.Serialize(new
		{
			source = "builtin",
			applications = new VrManifest[] { manifest }
		},
		new JsonSerializerOptions
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			WriteIndented = true,
			IncludeFields = true
		});

		if (path is null)
			path = ".vrmanifest";
		else if (!path.EndsWith(".vrmanifest"))
			path += ".vrmanifest";

		path = Path.Combine(Directory.GetCurrentDirectory(), path);
		File.WriteAllText(path, json);

		//Valve.VR.OpenVR.Applications.RemoveApplicationManifest(path);
		var error = Valve.VR.OpenVR.Applications.AddApplicationManifest(path, false);
		if (error != EVRApplicationError.None)
			return null;
		
		return path;
	}

    /// <summary>
    /// Removes a .vrmanifest file from OpenVR, effectively uninstalling the app from it.
    /// You should use this when updating the vrmanifests location.
    /// This has to be called after initializing vr.
    /// </summary>
    public bool UninstallApp(string vrmanifestPath)
		=> Valve.VR.OpenVR.Applications.RemoveApplicationManifest(vrmanifestPath) is EVRApplicationError.None;

    #region Draw Thread

    private DrawContext drawContext = null!;
	/// <summary>
	/// Returns the openvr CVR System, if initialized.
	/// This is useful for implementing not yet available features of OpenVR.NET.
	/// If you find yourself using unimplemented features, please open an issue
	/// </summary>
	public CVRSystem CVR { get; private set; } = null!;

	/// <summary>
	/// Allows drawing to the headset. Should be called on the draw thread. 
	/// Might block to synchronize with headset refresh rate, depending on headset drivers.
	/// It will also update the player pose, so eyes are positioned correctly.
	/// While not critical, you might want to want to synchronise this pose update with the update thread.
	/// Will return <see langword="null"/> until initialized.
	/// </summary>
	public IVRDrawContext? UpdateDraw(ETrackingUniverseOrigin origin = ETrackingUniverseOrigin.TrackingUniverseStanding)
	{
		if (State.HasFlag( VrState.OK))
		{
			PollPoses( origin );
			return drawContext;
		}
		else
			return null;
	}

    private bool hasFocus = true;

	public ETrackingUniverseOrigin TrackingOrigin { get; private set; } = (ETrackingUniverseOrigin)(-1);

    private readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
    private readonly Dictionary<int, (VrDevice device, VrDevice.Owner owner)> trackedDeviceOwners = new();
    private readonly HashSet<VrDevice> activeDevices = new();

	// although in theory this should be on the update thread,
	// this updates once per draw frame and is required to be called to allow for drawing
	private void PollPoses(ETrackingUniverseOrigin origin)
	{
		if (origin != TrackingOrigin)
		{
			TrackingOrigin = origin;
			Valve.VR.OpenVR.Compositor.SetTrackingSpace(origin);
		}

		var error = Valve.VR.OpenVR.Compositor.WaitGetPoses(renderPoses, Array.Empty<TrackedDevicePose_t>());
		if (error is EVRCompositorError.DoNotHaveFocus)
		{
			if (hasFocus)
			{
				hasFocus = false;
				Events.Log( $"Player pose could not be retreived", EventType.NoFous, error );
			}
		}
		else if (error != EVRCompositorError.None)
		{
			Events.Log( $"Player pose could not be retreived", EventType.CoundntFetchPlayerPose, error );
			return;
		}

		hasFocus = true;

		for (int i = 0; i < renderPoses.Length; i++)
		{
			var type = CVR.GetTrackedDeviceClass((uint)i);
			if (type is ETrackedDeviceClass.Invalid)
				continue;

			ref var pose = ref renderPoses[i];

			VrDevice device;
			VrDevice.Owner owner;
			if (!trackedDeviceOwners.TryGetValue(i, out var data))
			{
				device = type switch
				{
					ETrackedDeviceClass.HMD => new Headset(this, i, out owner),
					ETrackedDeviceClass.Controller => new Controller(this, i, out owner),
					ETrackedDeviceClass.GenericTracker => new Tracker(this, i, out owner),
					ETrackedDeviceClass.TrackingReference => new TrackingReference(this, i, out owner),
					ETrackedDeviceClass.DisplayRedirect => new DisplayRedirect(this, i, out owner),
					_ => new VrDevice(this, i, out owner)
				};

				trackedDeviceOwners.Add(i, (device, owner));

                void SetDetectedDevice()
                {
                    if (device is Headset headset)
                        Headset = headset;
                    else if (device is Controller controller)
                    {
                        if (controller.Role is ETrackedControllerRole.LeftHand)
                            LeftController = controller;
                        else if (controller.Role is ETrackedControllerRole.RightHand)
                            RightController = controller;
                    }

                    trackedDevices.Add(device);
                    DeviceDetected?.Invoke(device);
                }

                updateScheduler.Enqueue(SetDetectedDevice);
			}
			else 
			{
				(device, owner) = data;
			}

			if (pose.bDeviceIsConnected)
			{
				if (!activeDevices.Contains(device))
				{
					activeDevices.Add(device);
					inputScheduler.Enqueue(() => updateableInputDevices.Add((device, owner)));
					updateScheduler.Enqueue(() => owner.IsEnabled = true);
				}
			}
			else 
			{
				if (activeDevices.Contains(device))
				{
					activeDevices.Remove(device);

                    void SetDeviceInactive()
                    {
                        updateableInputDevices.Remove((device, owner));
                        if (device is Controller controller)
                            controller.OnTurnedOff();
                    }

                    inputScheduler.Enqueue(SetDeviceInactive);

                    void SetOwnerDisabled()
						=> owner.IsEnabled = false;

                    updateScheduler.Enqueue(SetOwnerDisabled);
				}
			}

			if (pose.bPoseIsValid)
			{
                Matrix4x4 matrix = pose.mDeviceToAbsoluteTracking.ToMatrix4x4();
				owner.RenderDeviceToAbsoluteTrackingMatrix = matrix;
				Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
				owner.RenderPosition = translation;
				owner.RenderRotation = rotation;
			}

			if (pose.eTrackingResult != owner.OffThreadTrackingState)
			{
				var state = pose.eTrackingResult;
				owner.OffThreadTrackingState = state;
				updateScheduler.Enqueue(() => owner.TrackingState = state);
			}
		}
	}
    #endregion

    #region Input Thread

    private readonly ConcurrentQueue<Action> inputScheduler = new();
    private readonly HashSet<(VrDevice device, VrDevice.Owner owner)> updateableInputDevices = new();
    private readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];

	/// <summary>
	/// Updates inputs. Should be called on the input or update thread.
	/// You can supply a time in seconds for openvr to predict input poses
	/// </summary>
	public void UpdateInput(float posePredictionTime = 0)
	{
		while (inputScheduler.TryDequeue(out var action)) 
			action();
		
		if (!State.HasFlag(VrState.OK))
			return;

		CVR.GetDeviceToAbsoluteTrackingPose(TrackingOrigin, posePredictionTime, gamePoses);
		foreach (var (device, owner) in updateableInputDevices)
		{
			ref var pose = ref gamePoses[device.DeviceIndex];
			if (!pose.bPoseIsValid)
				continue;

            Matrix4x4 matrix = pose.mDeviceToAbsoluteTracking.ToMatrix4x4();
            owner.DeviceToAbsoluteTrackingMatrix = matrix;
			Matrix4x4.Decompose(matrix, out _, out Quaternion rotation, out Vector3 translation);
            owner.Position = translation;
            owner.Rotation = rotation;
            owner.Velocity = new(pose.vVelocity.v0, pose.vVelocity.v1, pose.vVelocity.v2);
			owner.AngularVelocity = new(pose.vAngularVelocity.v0, pose.vAngularVelocity.v1, -pose.vAngularVelocity.v2);
		}

		if (actionSets != null)
		{
			var error = Valve.VR.OpenVR.Input.UpdateActionState( actionSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
			if (error != EVRInputError.None)
			{
				Events.Log("Could not read input state", EventType.CouldntGetInput, error);
				return;
			}

			foreach (var i in actions.Values)
				i.Update();
		}

		foreach (var i in updateableInputDevices)
			if (i.device is Controller controller)
				controller.UpdateInput();
	}
    #endregion

    #region Update Thread

    private readonly ConcurrentQueue<Action> updateScheduler = new();
    private bool isPaused = false;

	/// <summary>
	/// Invokes the update thread related events and polls vr events.
	/// </summary>
	public void Update()
	{
		while (updateScheduler.TryDequeue(out var action))
			action();
		
		if (!State.HasFlag(VrState.OK))
			return;

		if (Events.AnyOnOpenVrEventHandlers)
		{
			VREvent_t e = default;
			while (CVR.PollNextEvent(ref e, (uint)Marshal.SizeOf<VREvent_t>()))
			{
				var type = (EVREventType)e.eventType;
				var device = trackedDevices.FirstOrDefault( x => x.DeviceIndex == e.trackedDeviceIndex );
				var age = e.eventAgeSeconds;
				var data = e.data;

				Events.Event( type, device, age, data );
			}
		}

		if (CVR.ShouldApplicationPause() != isPaused)
		{
			isPaused = !isPaused;
			if (isPaused)
				UserDistracted?.Invoke();
			else
				UserFocused?.Invoke();
		}

		chaperone?.Update();

		foreach (var i in trackedDevices)
			i.Update();
	}

	/// <summary>
	/// Called on the update thread when the user is doing something else than interacting with the application,
	/// for example changing settings in the overlay
	/// </summary>
	public event Action? UserDistracted;
	/// <summary>
	/// Called on the update thread when the user stops interacting with something else than the application,
	/// for example changing settings in the overlay
	/// </summary>
	public event Action? UserFocused;

	#endregion

	private readonly HashSet<VrDevice> trackedDevices = new();

	public Headset? Headset { get; private set; }
	public Controller? LeftController { get; private set; }
	public Controller? RightController { get; private set; }

	/// <summary>
	/// All ever detected devices. This is safe to use on the update thread.
	/// </summary>
	public IEnumerable<VrDevice> TrackedDevices => trackedDevices;
	/// <summary>
	/// All enabled devices. This is safe to use on the update thread.
	/// </summary>
	public IEnumerable<VrDevice> ActiveDevices => trackedDevices.Where( x => x.IsEnabled );
	/// <summary> 
	/// Invoked when a new device is detected. Devices can never become "undetected". This is safe to use on the update thread.
	/// </summary>
	public event Action<VrDevice>? DeviceDetected;

	public bool IsHeadsetPresent => Valve.VR.OpenVR.IsHmdPresent();

	[MemberNotNullWhen( true, nameof( OpenVrRuntimePath ) )]
	public bool IsOpenVrRuntimeInstalled => Valve.VR.OpenVR.IsRuntimeInstalled();

	public string? OpenVrRuntimePath => Valve.VR.OpenVR.RuntimePath();

	public string? OpenVrRuntimeVersion => CVR.GetRuntimeVersion();

	private IActionManifest? actionManifest;
	private Dictionary<Enum, IAction> definedActions = new();
	private VRActiveActionSet_t[]? actionSets;

	/// <summary>
	/// Fetches an action defined in the action manifest
	/// </summary>
	public IAction ActionFor(Enum action)
		=> definedActions[action];

	/// <summary>
	/// Sets the action manifest. This method will throw if the manifest couldnt be set.
	/// Returns the absolute path to the action manifest
	/// </summary>
	public string SetActionManifest(IActionManifest manifest, string? path = null)
	{
		path ??= "ActionManifest.json";
		path = Path.Combine(Directory.GetCurrentDirectory(), path);
		File.WriteAllText(path, manifest.ToJson() );
		var error = Valve.VR.OpenVR.Input.SetActionManifestPath(path);
		if (error != EVRInputError.None)
			throw new Exception($"Could not set action manifest: {error}");

        void UpdateActionManifest()
        {
            actionManifest = null;
            definedActions.Clear();
            actions.Clear();

            foreach (var (i, _) in updateableInputDevices)
                if (i is Controller controller)
                    controller.actions.Clear();

            var actionSets = manifest.ActionSets.Select(set =>
            {
                ulong handle = 0;
                var error = Valve.VR.OpenVR.Input.GetActionSetHandle(set.Path, ref handle);
                if (error != EVRInputError.None)
                    Events.Log($"Could not get handle for action set {set.Name}", EventType.CoundntFetchActionSetHandle, error);

                return new VRActiveActionSet_t { ulActionSet = handle };
            }).ToArray();

            foreach (var set in manifest.ActionSets)
                foreach (var action in manifest.ActionsForSet(set))
                    definedActions.Add(action.Name, action);

            ETrackedControllerRole hand = ETrackedControllerRole.Invalid;
            error = Valve.VR.OpenVR.Input.GetDominantHand(ref hand);
            if (error is EVRInputError.None)
                DominantHand = hand;

            actionManifest = manifest;
            this.actionSets = actionSets;
            actionsLoaded?.Invoke();
            actionsLoaded = null;
        }

        inputScheduler.Enqueue(UpdateActionManifest);

		return path;
	}

	public ETrackedControllerRole DominantHand { get; private set; } = ETrackedControllerRole.Invalid;

	/// <summary>
	/// Binds an action to perform when the action manifest is loaded,
	/// or if its already loaded, its invoked immmediately.
	/// This is safe to call on the input thread
	/// </summary>
	public void BindActionsLoaded(Action action)
	{
		if (actionManifest != null)
			inputScheduler.Enqueue(action);
		else
			actionsLoaded += action;
	}

	private event Action? actionsLoaded;
	private readonly Dictionary<Enum, Input.Action> actions = new();

	/// <summary>
	/// Gets an action defined in the action manifest. This is safe to use on the input thread,
	/// as well as with <see cref="BindActionsLoaded(Action)"/>, as it will be scheduled to execute on the input thread
	/// </summary>
	/// <typeparam name="T">
	/// The action type, coresponding to the defined <see cref="ActionType"/>.
	/// Currently implemented ones are <see cref="Input.BooleanAction"/>, <see cref="Input.ScalarAction"/>,
	/// <see cref="Input.Vector2Action"/>, <see cref="Input.Vector3Action"/>, <see cref="Input.HapticAction"/>,
	/// <see cref="Input.PoseAction"/> and <see cref="Input.HandSkeletonAction"/>
	/// </typeparam>
	public T? GetAction<T>(Enum action, Controller? controller = null) where T : Input.Action
	{
		if (controller != null)
			return controller.GetAction<T>(action);

		if (!actions.TryGetValue(action, out var value))
		{
			var @params = definedActions[action];
			actions.Add( action, value = @params.CreateAction(this, null));
		}

		return value as T;
	}
}
