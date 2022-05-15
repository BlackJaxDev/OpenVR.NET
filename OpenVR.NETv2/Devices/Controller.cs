﻿using OpenVR.NET.Input;
using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;

namespace OpenVR.NET.Devices;

public class Controller : VrDevice {
	public Controller ( VR vr, int index, out Owner owner ) : base( vr, index, out owner ) {
		var error = ETrackedPropertyError.TrackedProp_Success;
		var roleID = Valve.VR.OpenVR.System.GetInt32TrackedDeviceProperty( (uint)index, ETrackedDeviceProperty.Prop_ControllerRoleHint_Int32, ref error );

		if ( error != ETrackedPropertyError.TrackedProp_Success ) {
			vr.Events.Log( $"Could not get controller role (device {index})", EventType.CoundntFetchControllerRole, (error, this) );
		}

		Role = (ETrackedControllerRole)roleID;

		if ( Role is ETrackedControllerRole.LeftHand or ETrackedControllerRole.RightHand ) {
			var error2 = Valve.VR.OpenVR.Input.GetInputSourceHandle( Role is ETrackedControllerRole.LeftHand
				? Valve.VR.OpenVR.k_pchPathUserHandLeft
				: Valve.VR.OpenVR.k_pchPathUserHandRight,
				ref Handle
			);

			if ( error2 != EVRInputError.None ) {
				Handle = Valve.VR.OpenVR.k_ulInvalidInputValueHandle;
				vr.Events.Log( $"Could not determine the input handle (device {DeviceIndex})", EventType.CoundntFetchControllerHandle, (error, this) );
			}
		}
		else Handle = Valve.VR.OpenVR.k_ulInvalidInputValueHandle;
	}

	public readonly ETrackedControllerRole Role = ETrackedControllerRole.Invalid;
	protected readonly ulong Handle;

	/// <summary>
	/// Update the devices input (on the input thread)
	/// </summary>
	public void UpdateInput () {
		if ( rawActions is null )
			return;

		VRControllerState_t state = default;
		if ( VR.CVR.GetControllerState( DeviceIndex, ref state, (uint)Marshal.SizeOf<VRControllerState_t>() ) && state.unPacketNum != lastPacketNum ) {
			lastPacketNum = state.unPacketNum;
			foreach ( var i in rawActions ) {
				i.Update( state );
			}
		}
	}

	/// <summary>
	/// Resets the devices input to default (on the input thread)
	/// </summary>
	public void OnTurnedOff () {
		if ( rawActions is null )
			return;

		VRControllerState_t state = default;
		foreach ( var i in rawActions ) {
			i.Update( state );
		}
	}

	uint lastPacketNum;
	List<ILegacyAction>? rawActions;
	/// <summary>
	/// Legacy action if you prefer this system. Default ones are <see cref="RawButton"/>, <see cref="RawSingle"/>, <see cref="RawVector2"/> and <see cref="RawHaptic"/>.
	/// If you know all possible actions in your program, you should use manifest actions.
	/// It is undefined behaviour to use this system along the new input system.
	/// </summary>
	public IEnumerable<ILegacyAction> LegacyActions {
		get {
			if ( rawActions != null )
				return rawActions;

			rawActions = new List<ILegacyAction>();

			//VRControllerState_t state = default; // for some reason openvr can return invalid button masks unless we call this
			//VR.CVR.GetControllerState( DeviceIndex, ref state, (uint)Marshal.SizeOf<VRControllerState_t>() );

			var error = ETrackedPropertyError.TrackedProp_Success;
			var buttonsMask = VR.CVR.GetUint64TrackedDeviceProperty( DeviceIndex, ETrackedDeviceProperty.Prop_SupportedButtons_Uint64, ref error );
			if ( error != ETrackedPropertyError.TrackedProp_Success ) {
				VR.Events.Log( $"Could not get available buttons (device {DeviceIndex})", EventType.CoundntFetchControllerButtons, (error, this) );
			}
			else {
				for ( int i = 0; i < 64; i++ ) {
					var mask = 1ul << i;
					if ( ( buttonsMask & mask ) == 0 )
						continue;

					rawActions.Add( new RawButton { SourceHandle = Handle, Mask = mask, Type = (EVRButtonId)i } );
				}
			}

			for ( int i = 0; i < Valve.VR.OpenVR.k_unControllerStateAxisCount; i++ ) {
				error = ETrackedPropertyError.TrackedProp_Success;
				var type = (EVRControllerAxisType)VR.CVR.GetInt32TrackedDeviceProperty( DeviceIndex, ETrackedDeviceProperty.Prop_Axis0Type_Int32 + i, ref error );

				if ( error is ETrackedPropertyError.TrackedProp_UnknownProperty )
					break;

				if ( error != ETrackedPropertyError.TrackedProp_Success ) {
					VR.Events.Log( $"Could not get the [{i}] input axis (device {DeviceIndex})", EventType.CoundntFetchControllerAxis, (error, this) );
					continue;
				}

				if ( type is EVRControllerAxisType.k_eControllerAxis_Trigger ) {
					rawActions.Add( new RawSingle { SourceHandle = Handle, Index = i, AxisType = type, Type = EVRButtonId.k_EButton_Axis0 + i } );
				}
				else if ( type is EVRControllerAxisType.k_eControllerAxis_TrackPad or EVRControllerAxisType.k_eControllerAxis_Joystick ) {
					rawActions.Add( new RawVector2 { SourceHandle = Handle, Index = i, AxisType = type, Type = EVRButtonId.k_EButton_Axis0 + i } );
				}
			}

			rawActions.Add( new RawHaptic( this ) );

			return rawActions;
		}
	}

	/// <summary>
	/// Legacy action if you prefer this system. Default ones are <see cref="RawButton"/>, <see cref="RawSingle"/> and <see cref="RawVector2"/>.
	/// If you know all possible actions in your program, you should use manifest actions.
	/// </summary>
	public interface ILegacyAction {
		void Update ( in VRControllerState_t state );
		/// <summary>
		/// The input type for input actions, or -1 for outputs
		/// </summary>
		EVRButtonId Type { get; }
	}
	public abstract class RawInputAction<T> : InputAction<T>, ILegacyAction where T : struct {
		public sealed override void Update () { }
		public abstract void Update ( in VRControllerState_t state );

		protected VRControllerAxis_t GetAxis ( int index, in VRControllerState_t state ) {
			return index switch {
				0 => state.rAxis0,
				1 => state.rAxis1,
				2 => state.rAxis2,
				3 => state.rAxis3,
				_ => state.rAxis4
			};
		}

		public sealed override Input.Action Clone ( ulong sourceHandle )
			=> throw new InvalidOperationException( $"Can not clone raw inputs" );

		public EVRButtonId Type { get; init; }
	}

	public class RawButton : RawInputAction<(bool pressed, bool touched)> {
		public ulong Mask { get; init; }

		public override void Update ( in VRControllerState_t state ) {
			Value = (
				( state.ulButtonPressed & Mask ) != 0,
				( state.ulButtonTouched & Mask ) != 0
			);
		}
	}

	public class RawSingle : RawInputAction<float> {
		public int Index { get; init; }
		public EVRControllerAxisType AxisType { get; init; }

		public override void Update ( in VRControllerState_t state ) {
			Value = GetAxis( Index, state ).x;
		}
	}

	public class RawVector2 : RawInputAction<Vector2> {
		public int Index { get; init; }
		public EVRControllerAxisType AxisType { get; init; }

		public override void Update ( in VRControllerState_t state ) {
			var axis = GetAxis( Index, state );
			Value = new( axis.x, axis.y );
		}
	}

	public class RawHaptic : ILegacyAction {
		VrDevice device;
		public RawHaptic ( VrDevice device ) {
			this.device = device;
		}

		public void Update ( in VRControllerState_t state ) { }

		public void TriggerVibration ( int axis, ushort durationMicroSeconds ) {
			device.VR.CVR.TriggerHapticPulse( device.DeviceIndex, (uint)axis, durationMicroSeconds );
		}

		public EVRButtonId Type => (EVRButtonId)( -1 );
	}
}
