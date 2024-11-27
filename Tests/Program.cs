﻿//#define LEGACY_INPUT

using OpenVR.NET;
using OpenVR.NET.Devices;
using OpenVR.NET.Input;
using OpenVR.NET.Manifest;
using SixLabors.ImageSharp;
using System.Collections.Concurrent;
using System.Text;
using Tests;
using Valve.VR;
using Action = System.Action;

bool running = true;
var vr = new VR();

ThreadStart createInterval ( int ms, Action init, System.Action<long, long> action, Action finalize ) {
	return () => {
		init();
		var lastTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		while ( running ) {
			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			var deltaTime = now - lastTime;
			lastTime = now;

			action( now, deltaTime );

			deltaTime = ms - deltaTime;
			Thread.Sleep( (int)( deltaTime >= 0 ? deltaTime : 0 ) );
		}
		finalize();
	};
}

object mutex = new();
void Log ( object? msg, Thread? expectedThread = null ) {
	lock ( mutex ) {
		if ( expectedThread != null && expectedThread != Thread.CurrentThread ) {
			Console.BackgroundColor = ConsoleColor.Red;
			Console.ForegroundColor = ConsoleColor.White;
			Console.WriteLine( $"v Invalid Thread! Expected [{expectedThread.Name} - {expectedThread.ManagedThreadId}]\n\tbut got [{Thread.CurrentThread.Name} | {Thread.CurrentThread.ManagedThreadId}] v" );
			Console.ResetColor();
		}

		Console.WriteLine( $"[{Thread.CurrentThread.Name} - {Thread.CurrentThread.ManagedThreadId}] {msg}" );
	}
}

Thread input = null!;
Thread update = null!;
Thread draw = null!;

input = new Thread( createInterval( 4, () => {
	Log( "Input thread started", input );
}, ( time, deltaTime ) => {
	vr.UpdateInput();
}, () => {
	Log( "Input thread stopped", input );
} ) ) { Name = "Input" };

bool hiddenMeshSaved = false;
draw = new Thread( createInterval( 17, () => {
	Log( "Draw thread started", draw );
}, ( time, deltaTime ) => {
	var ctx = vr.UpdateDraw();
	//if ( vr.Devices.OfType<Headset>().FirstOrDefault() is Headset headset ) {
	//	var p = headset.Position;
	//	var o = headset.RenderPosition - headset.Position;
	//	Log( $"HMD position: {{{p.X:N2}, {p.Y:N2}, {p.Z:N2}}} | render offset: {{{o.X:N2}, {o.Y:N2}, {o.Z:N2}}}" );
	//}
	if ( ctx is null )
		return;

	if ( !hiddenMeshSaved ) {
		hiddenMeshSaved = true;
		StringBuilder left = new();
		int i = 1;
		foreach ( var (a, b, c) in ctx.GetHiddenAreaMesh( EVREye.Eye_Left ) ) {
			left.AppendLine( $"v {a.X} {a.Y} 0" );
			left.AppendLine( $"v {b.X} {b.Y} 0" );
			left.AppendLine( $"v {c.X} {c.Y} 0" );
			left.AppendLine( $"f {i++} {i++} {i++}" );
		}

		StringBuilder right = new();
		i = 1;
		foreach ( var (a, b, c) in ctx.GetHiddenAreaMesh( EVREye.Eye_Right ) ) {
			right.AppendLine( $"v {a.X} {a.Y} 0" );
			right.AppendLine( $"v {b.X} {b.Y} 0" );
			right.AppendLine( $"v {c.X} {c.Y} 0" );
			right.AppendLine( $"f {i++} {i++} {i++}" );
		}

		Task.Run( async () => {
			await File.WriteAllTextAsync( "./leftHiddenMesh.obj", left.ToString() );
			Log( "Saved left eye hidden mesh to `./leftHiddenMesh.obj`" );
		} );
		Task.Run( async () => {
			await File.WriteAllTextAsync( "./rightHiddenMesh.obj", right.ToString() );
			Log( "Saved right eye hidden mesh to `./rightHiddenMesh.obj`" );
		} );

		StringBuilder leftinv = new();
		i = 1;
		foreach ( var (a, b, c) in ctx.GetHiddenAreaMesh( EVREye.Eye_Left, inverse: true ) ) {
			leftinv.AppendLine( $"v {a.X} {a.Y} 0" );
			leftinv.AppendLine( $"v {b.X} {b.Y} 0" );
			leftinv.AppendLine( $"v {c.X} {c.Y} 0" );
			leftinv.AppendLine( $"f {i++} {i++} {i++}" );
		}

		StringBuilder rightinv = new();
		i = 1;
		foreach ( var (a, b, c) in ctx.GetHiddenAreaMesh( EVREye.Eye_Right, inverse: true ) ) {
			rightinv.AppendLine( $"v {a.X} {a.Y} 0" );
			rightinv.AppendLine( $"v {b.X} {b.Y} 0" );
			rightinv.AppendLine( $"v {c.X} {c.Y} 0" );
			rightinv.AppendLine( $"f {i++} {i++} {i++}" );
		}

		Task.Run( async () => {
			await File.WriteAllTextAsync( "./leftInverseHiddenMesh.obj", leftinv.ToString() );
			Log( "Saved inverse left eye hidden mesh to `./leftInverseHiddenMesh.obj`" );
		} );
		Task.Run( async () => {
			await File.WriteAllTextAsync( "./rightInverseHiddenMesh.obj", rightinv.ToString() );
			Log( "Saved inverse right eye hidden mesh to `./rightInverseHiddenMesh.obj`" );
		} );

		StringBuilder leftloop = new();
		foreach ( var a in ctx.GetLoopHiddenAreaMesh( EVREye.Eye_Left ) ) {
			leftloop.AppendLine( $"v {a.X} {a.Y} 0" );
		}

		StringBuilder rightloop = new();
		foreach ( var a in ctx.GetLoopHiddenAreaMesh( EVREye.Eye_Right ) ) {
			rightloop.AppendLine( $"v {a.X} {a.Y} 0" );
		}

		Task.Run( async () => {
			await File.WriteAllTextAsync( "./leftLoopHiddenMesh.obj", leftloop.ToString() );
			Log( "Saved loop left eye hidden mesh to `./leftLoopHiddenMesh.obj`" );
		} );
		Task.Run( async () => {
			await File.WriteAllTextAsync( "./rightLoopHiddenMesh.obj", rightloop.ToString() );
			Log( "Saved loop right eye hidden mesh to `./rightLoopHiddenMesh.obj`" );
		} );
	}
}, () => {
	Log( "Draw thread stopped", draw );
} ) ) { Name = "Draw" };

ConcurrentQueue<Action> updateQueue = new();
update = new Thread( createInterval( 6, () => {
	Log( "Update thread started", update );
}, ( time, deltaTime ) => {
	vr.Update();
	while ( updateQueue.TryDequeue( out var action ) ) {
		action();
	}
}, () => {
	Log( "Update thread stopped", update );
} ) ) { Name = "Update" };

ConcurrentDictionary<int, string> textures = new();
vr.DeviceDetected += d => {
	Log( $"Device detected: {d} | {d.TrackingState}\n Model name: `{d.Model?.Name}`", update );
	if ( d.Model is DeviceModel model ) {
		void save ( ComponentModel comp ) {
			StringBuilder sb = new();
			var name = $"{( comp.ParentName is null ? "" : $"{comp.ParentName}+" )}{comp.Name}".Replace( '/', '-' ).Replace( '\\', '-' );
			_ = comp.LoadAsync( addTexture: async image => {
				if ( textures.TryAdd( image.ID, $"./{name}.png" ) ) {
					var tx = await image.LoadImage();
					if ( tx != null ) {
						await tx.SaveAsPngAsync( $"./{name}.png" );
						Log( $"Saved model texture at `./{name}.png`" );
						tx.Dispose();
					}
					else {
						Log( $"Error: couldnt save texture at `./{name}.png`" );
					}
				}
				else {
					textures.TryGetValue( image.ID, out var path );
					Log( $"{name} shares texture at `{path}`" );
				}
			}, onError: ( err, ctx ) => {
				Log( $"Error loading {ctx} `{name}`: {err}" );
			}, addVertex: ( v, n, uv ) => {
				sb.AppendLine( $"v {v.X} {v.Y} {v.Z}" );
				sb.AppendLine( $"vt {uv.X} {uv.Y}" );
			}, addTriangle: ( a, b, c ) => {
				sb.AppendLine( $"f {a + 1}/{a + 1}/{a + 1} {b + 1}/{b + 1}/{b + 1} {c + 1}/{c + 1}/{c + 1}" );
			}, finish: async type => {
				if ( type is ComponentModel.ComponentType.Component ) {
					if ( !File.Exists( $"./{name}.obj" ) )
						await File.WriteAllTextAsync( $"./{name}.obj", sb.ToString() );
					Log( $"Saved model at `./{name}.obj`" );
				}
				else if ( type is ComponentModel.ComponentType.ReferencePoint ) {
					Log( $"{name} is a reference point`" );
				}
			} );
		}

		save( model.JoinedModel );
		foreach ( var i in model.Components ) {
			save( i );
		}
	}

	d.Enabled += () => {
		Log( $"Device {d} enabled", update );
	};
	d.Disabled += () => {
		Log( $"Device {d} disabled", update );
	};

	d.TrackingStateChanged += state => {
		Log( $"{d} (device {d.DeviceIndex}) changed tracking state to {state}", update );
	};

	if ( d is not Controller c )
		return;

#if LEGACY_INPUT
	bool logInput = true;
	foreach ( var i in c.LegacyActions ) {
		if ( i is Controller.RawButton b ) {
			Log( $"{c.Role} has Button {i.Type} [{Math.Log2( b.Mask ):0}]" );
			if ( logInput ) b.ValueUpdated += v => {
				Log( $"{c.Role} Button {i.Type} [{Math.Log2( b.Mask ):0}] Pressed = {v.pressed} Touched = {v.touched}" );
			};
		}
		else if ( i is Controller.RawSingle s ) {
			Log( $"{c.Role} has Scalar {i.Type}/{s.AxisType} [{s.Index}]" );
			if ( logInput ) s.ValueUpdated += v => {
				Log( $"{c.Role} {i.Type}/{s.AxisType} [{s.Index}] X = {v}" );
			};
		}
		else if ( i is Controller.RawVector2 v2 ) {
			Log( $"{c.Role} has Vector2 {i.Type}/{v2.AxisType} [{v2.Index}]" );
			if ( logInput ) v2.ValueUpdated += v => {
				Log( $"{c.Role} {i.Type}/{v2.AxisType} [{v2.Index}] X = {v.X:N4}, Y = {v.Y:N4}" );
			};
		}
		else if ( i is Controller.RawHaptic h ) {
			Log( $"{c.Role} has raw haptic" );
		}
		else {
			Log( $"{c.Role} has raw input {i} ({i.Type})" );
		}
	}

	var haptic = c.LegacyActions.OfType<Controller.RawHaptic>().FirstOrDefault();
	void runTest () {
		Task.Run( async () => {
			for ( int axis = 0; axis < 5; axis++ ) {
				Log( $"Running haptics test on controller {c.Role} (device{c.DeviceIndex}) - Axis {axis}" );
				for ( int i = 0; i < 20; i++ ) {
					haptic.TriggerVibration( axis, ushort.MaxValue );
					await Task.Delay( ushort.MaxValue / 500 );
				}
			}
		} );
	}

	if ( haptic != null ) {
		if ( c.IsEnabled ) {
			runTest();
		}

		c.Enabled += runTest;
	}
#else
	vr.BindActionsLoaded( () => {
		var boolean = vr.GetAction<BooleanAction>( Actions.Boolean )!;
		boolean.ValueUpdated += v => Log( $"Boolean changed to {v}" );
		var scalar = vr.GetAction<ScalarAction>( Actions.Scalar )!;
		scalar.ValueUpdated += v => Log( $"Scalar changed to {v}" );
		var vector2 = vr.GetAction<Vector2Action>( Actions.Vector2 )!;
		vector2.ValueUpdated += v => Log( $"Vector2 changed to {v}" );
		var vector3 = vr.GetAction<Vector3Action>( Actions.Vector3 )!;
		vector3.ValueUpdated += v => Log( $"Vector3 changed to {v}" );
	} );
#endif
};
vr.Events.OnLog += ( msg, type, ctx ) => {
	if ( ctx != null )
		Log( $"{type} | {msg} ({ctx.GetType().Name} - {ctx})" );
	else
		Log( $"{type} | {msg}" );
};

vr.Events.OnOpenVrEvent += ( EVREventType type, VrDevice? device, float age, in VREvent_Data_t data ) => {
	return;

	object? getProp ( ETrackedDeviceProperty prop, VrDevice device ) {
		uint deviceIndex = device.DeviceIndex;
		ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
		if ( prop.ToString().EndsWith( "_Bool" ) )
			return vr.CVR.GetBoolTrackedDeviceProperty( deviceIndex, prop, ref error );
		else if ( prop.ToString().EndsWith( "_Float" ) )
			return vr.CVR.GetFloatTrackedDeviceProperty( deviceIndex, prop, ref error );
		else if ( prop.ToString().EndsWith( "_Int32" ) )
			return vr.CVR.GetInt32TrackedDeviceProperty( deviceIndex, prop, ref error );
		else if ( prop.ToString().EndsWith( "_Uint64" ) )
			return vr.CVR.GetUint64TrackedDeviceProperty( deviceIndex, prop, ref error );
		else if ( prop.ToString().EndsWith( "_String" ) ) {
			StringBuilder sb = new( 256 );
			vr.CVR.GetStringTrackedDeviceProperty( deviceIndex, prop, sb, 256, ref error );
			return sb.ToString();
		}

		return null;
	}

	if ( type is EVREventType.VREvent_ButtonPress
		or EVREventType.VREvent_ButtonTouch
		or EVREventType.VREvent_ButtonUnpress
		or EVREventType.VREvent_ButtonUntouch ) {
		return;
		//if ( data.device != null ) {
		//	Log( $"{data.type} | (device {data.device.DeviceIndex}) Button {data.data.controller.button}" );
		//	return;
		//}
	}

	if ( type is EVREventType.VREvent_ActionBindingReloaded
		or EVREventType.VREvent_Input_BindingLoadSuccessful
		or EVREventType.VREvent_Input_BindingsUpdated ) {
		return;
	}

	if ( type is EVREventType.VREvent_Input_HapticVibration ) {
		return;
	}

	void log ( string msg )
		=> Log( msg, update );

	if ( type is EVREventType.VREvent_PropertyChanged && device != null ) {
		log( $"{type} | (device {device.DeviceIndex}) {data.property.prop} = {getProp( data.property.prop, device )}" );
	}
	else {
		if ( device != null )
			log( $"{type} | {device} (device {device.DeviceIndex})" );
		else
			log( $"{EventType.VrEvent} | {type}" );
	}
};

if ( vr.IsOpenVrRuntimeInstalled ) {
	var path = vr.OpenVrRuntimePath;
	Log( $"OpenVR runtime path: `{path}`" );
	Log( $"Headset is {( vr.IsHeadsetPresent ? "" : "not " )}present" );

	if ( !vr.TryStart() ) {
		return;
	}

	Log( $"OpenVR runtime version: `{vr.OpenVrRuntimeVersion}`" );

#if LEGACY_INPUT
	var actionManifestPath = null;
#else
	var actionManifestPath = vr.SetActionManifest( new ActionManifest<ActionSet, Actions> {
		ActionSets = new() {
			new() { Name = ActionSet.Main, Type = ActionSetType.LeftRight },
			new() { Name = ActionSet.Aux, Type = ActionSetType.LeftRight }
		},
		Actions = new() {
			new() { Category = ActionSet.Main, Name = Actions.Boolean, Type = ActionType.Boolean },
			new() { Category = ActionSet.Main, Name = Actions.Scalar, Type = ActionType.Scalar },
			new() { Category = ActionSet.Main, Name = Actions.Vector2, Type = ActionType.Vector2 },
			new() { Category = ActionSet.Main, Name = Actions.Vector3, Type = ActionType.Vector3 },
			new() { Category = ActionSet.Main, Name = Actions.Vibration, Type = ActionType.Vibration }
		}
	} );
#endif

	path = vr.InstallApp( new() {
		AppKey = "OpenVR.NET.Tests",
		WindowsPath = "Tests.exe",
		ActionManifestPath = actionManifestPath,
		LocalizedNames = new() {
			["en_us"] = (
				"OpenVR.NET Tests",
				"OpenVR.NET API tests"
			)
		}
	} );
	Log( $".vrmanifest path: `{path}`" );

	var bounds = vr.Chaperone.PlayfieldBounds;

	update.Start();
	input.Start();
	draw.Start();

	var threads = new[] { update, input, draw };
	Console.ReadKey( true );
	running = false;
	while ( threads.Any( x => x.ThreadState is System.Threading.ThreadState.Running ) ) {
		await Task.Delay( 100 );
	}
	Log( "Exiting..." );
	vr.Exit();
}
else {
	Log( "An OpenVR runtime is not intalled" );
}