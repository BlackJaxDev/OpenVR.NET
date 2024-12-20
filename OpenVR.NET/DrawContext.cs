﻿using System.Numerics;
using Valve.VR;

namespace OpenVR.NET;

public interface IVRDrawContext
{
	Vector2 Resolution { get; }

	/// <summary>
	/// Projection for the given eye. To generate a correct matrix from draw information it should be
	/// <c>GetProjectionMatrix * GetEyeToHeadMatrix.Inverse * inverseHeadTransform</c> (where left side gets applied last)
	/// </summary>
	Matrix4x4 GetProjectionMatrix ( EVREye eye, float nearPlane, float farPlane );

	(float left, float right, float bottom, float top) GetProjectionMatrixParams (EVREye eye);

	/// <summary>
	/// Eye to head matrix. Invert to get head to eye. To generate a correct matrix from draw information it should be
	/// <c>GetProjectionMatrix * GetEyeToHeadMatrix.Inverse * inverseHeadTransform</c> (where left side gets applied last)
	/// </summary>
	Matrix4x4 GetEyeToHeadMatrix ( EVREye eye );

	/// <summary>
	/// Submits the image for an eye. Can only be called after <see cref="UpdateDraw(double)"/>
	/// </summary>
	void SubmitFrame ( EVREye eye, Texture_t texture, EVRSubmitFlags flags = EVRSubmitFlags.Submit_Default );

	/// <summary>
	/// Gets a mesh which will cover the area of the render texture that is not visible ( or visible if <paramref name="inverse"/> is <see langword="true"/> ).
	/// The mesh is in UV coordinates.
	/// </summary>
	ReadOnlySpan<(Vector2, Vector2, Vector2)> GetHiddenAreaMesh ( EVREye eye, bool inverse = false );

	/// <summary>
	/// Gets a vertex loop around the area of the render texture which is visible.
	/// The mesh is in UV coordinates.
	/// </summary>
	ReadOnlySpan<Vector2> GetLoopHiddenAreaMesh ( EVREye eye );

	/// <summary>
	/// Whether OpenVR is rendering controllers on its own - you should consider not drawing user hands/controllers if this is <see langword="true"/>
	/// </summary>
	bool OverlayRendersControllers { get; }

	/// <summary>
	/// Returns true if OpenVR is doing significant rendering work and the game should do what it can to reduce
	/// its own workload, for example by reducing the size of the render target for each eye
	/// </summary>
	bool ShouldReduceWorkload { get; }
}

class DrawContext : IVRDrawContext
{
	VR VR;

	public DrawContext(VR vr)
	{
		VR = vr;
		uint w = 0, h = 0;
		VR.CVR.GetRecommendedRenderTargetSize( ref w, ref h );
		Resolution = new Vector2( w, h );
	}

	public void SubmitFrame(EVREye eye, Texture_t texture, EVRSubmitFlags flags = EVRSubmitFlags.Submit_Default)
	{
		VRTextureBounds_t bounds = new() { uMin = 0, uMax = 1, vMin = 0, vMax = 1 };
		var error = Valve.VR.OpenVR.Compositor.Submit( eye, ref texture, ref bounds, flags);
		if (error != EVRCompositorError.None)
			VR.Events.Log( $"Could not sumbit frame for {(eye is EVREye.Eye_Left ? "left" : "right")} eye", EventType.FrameSubmitError, (error, eye));
	}

	public Vector2 Resolution { get; }

	public ReadOnlySpan<(Vector2, Vector2, Vector2)> GetHiddenAreaMesh(EVREye eye, bool inverse = false)
	{
		var mesh = VR.CVR.GetHiddenAreaMesh(eye, inverse 
			? EHiddenAreaMeshType.k_eHiddenAreaMesh_Inverse
			: EHiddenAreaMeshType.k_eHiddenAreaMesh_Standard);

		unsafe 
		{
			return new ReadOnlySpan<(Vector2, Vector2, Vector2)>( mesh.pVertexData.ToPointer(), (int)mesh.unTriangleCount );
		}
	}

	public ReadOnlySpan<Vector2> GetLoopHiddenAreaMesh(EVREye eye)
	{
		var mesh = VR.CVR.GetHiddenAreaMesh(eye, EHiddenAreaMeshType.k_eHiddenAreaMesh_LineLoop);
		unsafe
		{
			return new ReadOnlySpan<Vector2>( mesh.pVertexData.ToPointer(), (int)mesh.unTriangleCount );
		}
	}

	public Matrix4x4 GetProjectionMatrix(EVREye eye, float nearPlane, float farPlane)
	{
		var matrix = VR.CVR.GetProjectionMatrix(eye, nearPlane, farPlane);
		return new Matrix4x4
		(
			matrix.m0, matrix.m1, matrix.m2, matrix.m3,
			matrix.m4, matrix.m5, matrix.m6, matrix.m7,
			matrix.m8, matrix.m9, matrix.m10, matrix.m11,
			matrix.m12, matrix.m13, matrix.m14, matrix.m15
		);
	}

	public Matrix4x4 GetEyeToHeadMatrix(EVREye eye)
	{
		var matrix = VR.CVR.GetEyeToHeadTransform(eye);
		return new Matrix4x4
		(
			matrix.m0, matrix.m1, matrix.m2, matrix.m3,
			matrix.m4, matrix.m5, matrix.m6, matrix.m7,
			matrix.m8, matrix.m9, matrix.m10, matrix.m11,
			0, 0, 0, 1
		);
	}

	public (float left, float right, float bottom, float top) GetProjectionMatrixParams(EVREye eye)
	{
		float left = 0, right = 0, bottom = 0, top = 0;
		VR.CVR.GetProjectionRaw( eye, ref left, ref right, ref top, ref bottom );
		return (left, right, bottom, top);
	}

	public bool OverlayRendersControllers
		=> VR.CVR.IsSteamVRDrawingControllers();

	public bool ShouldReduceWorkload
		=> VR.CVR.ShouldApplicationReduceRenderingWork();
}
