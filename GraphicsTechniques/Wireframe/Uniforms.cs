using System.Numerics;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct Uniforms
{
	public Matrix4x4 WorldViewProjectionMatrix;
	public Matrix4x4 WorldMatrix;
	public Vector4 Color;
}
