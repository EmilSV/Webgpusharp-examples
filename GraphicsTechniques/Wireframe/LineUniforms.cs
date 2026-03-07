using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct LineUniforms
{
	public uint Stride;
	public float Thickness;
	public float AlphaThreshold;
	private readonly float _pad0;
}
